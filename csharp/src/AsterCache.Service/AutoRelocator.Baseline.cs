using AsterCache.Core.Logging;
using AsterCache.Core.Plan;
using AsterCache.Core.Settings;
using AsterCache.Windows;

namespace AsterCache.Service;

/// AutoRelocator 의 재배치 전 디스크 기준 속도 측정과 재배치 뒤 회복 감시 시작.
public sealed partial class AutoRelocator
{
    // 쓰기 직전 기준 속도: 대상 존 (이미 할당돼 있을 때만: 빈 영역은 SMR 이 0 을 즉시 돌려 과대) 과 존에서 4 GiB 뒤의 할당된 기준 구간을 같은 1~3 회전 칸에서 재 중앙값으로 집계합니다.
    // 각 칸은 존·참조가 ProbeRound 하나 (192 MiB·10 s, token 취소) 를 나눠 씁니다. 못 재거나 회전 간격이 없으면 Baseline null (고정 분), 게임 시작·바쁨이면 Interrupted (이번 틱에 재배치하지 않음) 입니다.
    private (DriveBaseline? Baseline, bool Interrupted) MeasureBaseline(GameSettings g, PlanFile plan, CancellationToken token)
    {
        try
        {
            using DiskReadCoordinator.Lease? lease = _coordinator?.TryBegin(g.OriginRoot, "auto relocation baseline", token);
            bool CanIssue() => !token.IsCancellationRequested && (_coordinator is null || lease is { IsValid: true });
            string drive = DriveOf(g);
            bool GameRunning()
            {
                try
                {
                    return _env.GameRunningOnDisk?.Invoke(drive) ?? true;
                }
                catch (Exception)
                {
                    return true;
                }
            }

            bool CanMeasure() => CanIssue() && !GameRunning() && !DriveRecovery.Busy(_env, drive);
            if (!CanMeasure())
            {
                return (null, true);
            }

            CancellationToken probeToken = lease?.Token ?? token;

            bool WaitForQuietObservation()
            {
                TimeSpan remaining = BaselineBusyObservation;
                while (remaining > TimeSpan.Zero)
                {
                    if (!CanIssue() || GameRunning())
                    {
                        return false;
                    }

                    TimeSpan slice = remaining < BaselineBusyPoll ? remaining : BaselineBusyPoll;
                    if (!_env.Wait(slice, probeToken))
                    {
                        return false;
                    }

                    remaining -= slice;
                }

                return CanMeasure();
            }

            long stepLcn = _env.ProbeStepLcn(drive);
            if (stepLcn <= 0)
            {
                return (null, false); // 회전 위치를 보장할 수 없으면 비교 가능한 기준을 만들지 않음
            }

            if (!CanMeasure())
            {
                return (null, true);
            }

            if (plan.Zone.StartLcn is not { } zone)
            {
                return (null, false);
            }

            var located = _env.LocateProbeSpots(drive, zone, probeToken);
            if (!CanMeasure())
            {
                return (null, true);
            }

            if (located is not { } spots || (!spots.ZoneAllocated && spots.ReferenceLcn is null))
            {
                return (null, false);
            }

            int parts = (spots.ZoneAllocated ? 1 : 0) + (spots.ReferenceLcn is null ? 0 : 1);
            List<double> zoneSpeeds = [];
            List<double> refSpeeds = [];
            bool zoneMeasured = spots.ZoneAllocated;
            bool refMeasured = spots.ReferenceLcn is not null;
            for (int rotationIndex = 1; rotationIndex < DiskProbe.RotationSteps; rotationIndex++)
            {
                var round = new ProbeRound(probeToken, parts);
                if (zoneMeasured)
                {
                    if (!CanIssue() || GameRunning())
                    {
                        return (null, true);
                    }

                    ProbeSpot? zoneSample = Spot(drive, checked(zone + stepLcn * rotationIndex), round);
                    if (zoneSample is { } z)
                    {
                        zoneSpeeds.Add(z.Mbps);
                    }
                    else
                    {
                        zoneMeasured = false;
                    }
                }

                if (refMeasured && spots.ReferenceLcn is { } reference)
                {
                    if (!CanIssue() || GameRunning())
                    {
                        return (null, true);
                    }

                    ProbeSpot? refSample = Spot(drive, checked(reference + stepLcn * rotationIndex), round);
                    if (refSample is { } r)
                    {
                        refSpeeds.Add(r.Mbps);
                    }
                    else
                    {
                        refMeasured = false;
                    }
                }

                if (rotationIndex < DiskProbe.RotationSteps - 1 && (zoneMeasured || refMeasured) && !WaitForQuietObservation())
                {
                    return (null, true);
                }
            }

            if (!CanIssue() || GameRunning())
            {
                return (null, true);
            }

            ProbeSpot? zoneSpot = zoneMeasured && zoneSpeeds.Count == DiskProbe.RotationSteps - 1
                ? new ProbeSpot(zone, MedianBaseline(zoneSpeeds))
                : null;
            ProbeSpot? refSpot = refMeasured && refSpeeds.Count == DiskProbe.RotationSteps - 1
                ? new ProbeSpot(spots.ReferenceLcn!.Value, MedianBaseline(refSpeeds))
                : null;
            return (zoneSpot is null && refSpot is null ? null : new DriveBaseline(zoneSpot, refSpot), false);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            _log.Warn($"{g.Name}: baseline read speed not measured ({e.GetType().Name})");
            return (null, false);
        }
    }

    private static double MedianBaseline(IEnumerable<double> values) => values.Order().ElementAt((DiskProbe.RotationSteps - 1) / 2);

    private ProbeSpot? Spot(string drive, long lcn, ProbeRound round) => _env.ProbeMbps(drive, lcn, round) is > 0 and double mbps ? new ProbeSpot(lcn, mbps) : null;

    // 쓴 바이트가 있으면 회복 추적을 시작: probe 는 기준이 있을 때 최대 recovery_max_min 분 (그때까지 회복을 확인하지 못하면 recovery_hard_max_min 분까지 재배치만 계속 보류), 기준이 없거나 fixed 면 post_update_delay_min 분 고정, off 는 기다리지 않음.
    private void BeginRecovery(GameSettings g, AutoSettings auto, long movedBytes, DriveBaseline? baseline)
    {
        if (movedBytes <= 0 || auto.PostRelocateRecovery == AutoSettings.RecoveryOff)
        {
            return;
        }

        bool probe = auto.PostRelocateRecovery == AutoSettings.RecoveryProbe && baseline is not null;
        _recovery.Begin(DriveOf(g), probe ? baseline : null, TimeSpan.FromMinutes(probe ? auto.RecoveryMaxMin : auto.PostUpdateDelayMin), TimeSpan.FromMinutes(auto.RecoveryHardMaxMin));
    }
}
