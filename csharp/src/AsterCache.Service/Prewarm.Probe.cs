using System.ComponentModel;
using AsterCache.Core;
using AsterCache.Core.Json;
using AsterCache.Core.Plan;
using AsterCache.Core.Prefetch;
using AsterCache.Core.Settings;
using AsterCache.Windows;

namespace AsterCache.Service;

/// Prewarmer 의 읽기 시간 표본 (probe) 과 실제 상주 여부 판정.
public sealed partial class Prewarmer
{
    // 핫 집합 몇 곳을 읽어 이미 파일 캐시에 있으면 (CacheProbe) 다시 읽지 않습니다. 표본을 못 읽으면 모름 (null) 입니다.
    internal bool? AlreadyWarm(GameSettings g)
    {
        ProbeGeneration? before = ProbeGenerationOf(g);
        ProbeResult? p;
        try
        {
            p = ColdSamples(g) is { } samples ? ProbeSamplesForGame(g, samples, "already warm") : ProbeReadMsForGame(g, "already warm");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or PlanRejectedException or PyValueException or Win32Exception)
        {
            return null;
        }

        if (p is not { Samples: > 0 })
        {
            return null;
        }

        bool warm = !p.Cold;
        ProbeGeneration? after = ProbeGenerationOf(g);
        lock (_gate)
        {
            if (before is not null && before == after)
            {
                _residencyProbes[g.Name] = new ResidencyProbe(warm, _now(), before);
            }
            else
            {
                _residencyProbes.Remove(g.Name);
            }
        }

        return warm;
    }

    /// 같은 계획·trace 세대에서 얻은 마지막 실제 warm 표본 시각. 세대가 다르거나 확인할 수 없으면 기록을 버립니다.
    internal DateTimeOffset? RecentWarmProbeAt(GameSettings g)
    {
        ProbeGeneration? current = ProbeGenerationOf(g);
        lock (_gate)
        {
            if (!_residencyProbes.TryGetValue(g.Name, out ResidencyProbe? probe))
            {
                return null;
            }

            if (current is null || current != probe.Generation)
            {
                _residencyProbes.Remove(g.Name);
                return null;
            }

            return probe.Warm ? probe.At : null;
        }
    }

    private ProbeGeneration? ProbeGenerationOf(GameSettings game)
    {
        try
        {
            ProbeFileGeneration FileGeneration(string path)
            {
                if (!File.Exists(path))
                {
                    return new ProbeFileGeneration(path, false, 0, 0);
                }

                Fingerprint stamp = _env.FileStamp(path);
                return new ProbeFileGeneration(path, true, stamp.Size, stamp.MtimeNs);
            }

            return new ProbeGeneration(FileGeneration(game.Plan), FileGeneration(game.Trace), FileGeneration(PlanOutput.WarmOnlyPath(game.Trace)));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    private void InvalidateResidencyProbe(string game)
    {
        lock (_gate)
        {
            _residencyProbes.Remove(game);
        }
    }

    // 파일 한 개의 표본 (큰 파일은 조각마다 고르게) 이 모두 파일 캐시에 있으면 true. 표본을 못 읽거나 취소되면 false.
    private (ProbeResult? Result, bool Aborted) ProbeFileSamples(DiskReadCoordinator.Lease? lease, IReadOnlyList<(string Path, long Offset, int Bytes)> samples, CancellationToken ct)
    {
        bool CanIssue() => !ct.IsCancellationRequested && (_coordinator is null || lease is { IsValid: true });
        if (!CanIssue())
        {
            return (null, true);
        }

        if (_coordinator is null && !ct.CanBeCanceled)
        {
            return (_env.ProbeFileSamples(samples, ct), false);
        }

        if (_env.ProbeFileSamplesChecked is { } checkedProbe)
        {
            ProbeResult? result = checkedProbe(samples, lease?.Token ?? ct, CanIssue);
            return CanIssue() ? (result, false) : (null, true);
        }

        var results = new List<ProbeResult>();
        CancellationToken probeToken = lease?.Token ?? ct;
        foreach (var sample in samples)
        {
            if (!CanIssue())
            {
                return (null, true);
            }

            if (_env.ProbeFileSamples([sample], probeToken) is { } result)
            {
                results.Add(result);
            }
        }

        return CanIssue() ? (CombineProbes(results), false) : (null, true);
    }

    internal ProbeResult? ProbeSamplesForGame(GameSettings game, IReadOnlyList<(string Path, long Offset, int Bytes)> samples, string purpose, CancellationToken ct = default)
    {
        using DiskReadCoordinator.Lease? lease = _coordinator?.TryBegin(game.OriginRoot, purpose, ct);
        if (_coordinator is not null && (lease is null || !lease.IsValid))
        {
            return null;
        }

        bool CanIssue() => !ct.IsCancellationRequested && (_coordinator is null || lease is { IsValid: true });
        if (!CanIssue())
        {
            return null;
        }

        if (_coordinator is null && !ct.CanBeCanceled)
        {
            return _env.ProbeSamples(samples);
        }

        if (_env.ProbeSamplesChecked is { } checkedProbe)
        {
            ProbeResult? result = checkedProbe(samples, CanIssue);
            return CanIssue() ? result : null;
        }

        var results = new List<ProbeResult>();
        foreach (var sample in samples)
        {
            if (!CanIssue())
            {
                return null;
            }

            if (_env.ProbeSamples([sample]) is { } result)
            {
                results.Add(result);
            }
        }

        return CanIssue() ? CombineProbes(results) : null;
    }

    // 단발 표본 읽기를 디스크 대여 안에서: 거절·무효·취소면 읽지 않고 null (refused = true: 읽기 실패와 구분), 발행 직전 재검사를 받는 읽기가 있으면 그것을 씁니다.
    private ProbeResult? GuardedProbe(GameSettings game, string purpose, CancellationToken ct, Func<ProbeResult?> plain, Func<Func<bool>, ProbeResult?>? checkedProbe, out bool refused)
    {
        using DiskReadCoordinator.Lease? lease = _coordinator?.TryBegin(game.OriginRoot, purpose, ct);
        bool CanIssue() => !ct.IsCancellationRequested && (_coordinator is null || lease is { IsValid: true });
        refused = !CanIssue();
        if (refused)
        {
            return null;
        }

        ProbeResult? result = checkedProbe is null || (_coordinator is null && !ct.CanBeCanceled) ? plain() : checkedProbe(CanIssue);
        refused = !CanIssue();
        return refused ? null : result;
    }

    private ProbeResult? ProbeReadMsForGame(GameSettings game, string purpose, CancellationToken ct = default, bool partial = false)
    {
        using DiskReadCoordinator.Lease? lease = _coordinator?.TryBegin(game.OriginRoot, purpose, ct);
        if (_coordinator is not null && (lease is null || !lease.IsValid))
        {
            return null;
        }

        bool CanIssue() => !ct.IsCancellationRequested && (_coordinator is null || lease is { IsValid: true });
        if (!CanIssue())
        {
            return null;
        }

        if (_coordinator is null && !ct.CanBeCanceled)
        {
            return partial ? (_env.ProbePartialReadMs ?? _env.ProbeReadMs)(game) : _env.ProbeReadMs(game);
        }

        Func<GameSettings, Func<bool>, ProbeResult?>? checkedProbe = partial ? _env.ProbePartialReadMsChecked ?? _env.ProbeReadMsChecked : _env.ProbeReadMsChecked;
        ProbeResult? result = checkedProbe is null
            ? partial ? (_env.ProbePartialReadMs ?? _env.ProbeReadMs)(game) : _env.ProbeReadMs(game)
            : checkedProbe(game, CanIssue);
        return CanIssue() ? result : null;
    }

    private static ProbeResult? CombineProbes(IReadOnlyList<ProbeResult> results)
    {
        int samples = results.Sum(result => result.Samples);
        return samples == 0 ? null : new ProbeResult(samples, results.Sum(result => result.Slow), results.Sum(result => result.AverageMs * result.Samples) / samples);
    }

    private bool? FileCached(GameSettings game, ReadOp op, CancellationToken ct)
    {
        try
        {
            using DiskReadCoordinator.Lease? lease = _coordinator?.TryBegin(game.OriginRoot, "warm file cache probe", ct);
            if (_coordinator is not null && (lease is null || !lease.IsValid))
            {
                return null;
            }

            (ProbeResult? probe, bool aborted) = ProbeFileSamples(lease, CacheProbe.PickSamples(FileWarm.Pieces(op)), ct);
            return aborted ? null : probe is { Samples: > 0, Cold: false };
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// 재예열 점검의 표본 측정: 부분 예열 상태면 읽은 앞부분의 잘라낸 목록에서만, 아니면 평소대로.
    internal ProbeResult? ProbeFront(GameSettings g, CancellationToken ct = default) =>
        MemoryPartialPending(g.Name) ? ProbeReadMsForGame(WarmedPart(g), "rewarm sample", ct, partial: true) : ProbeReadMsForGame(g, "rewarm sample", ct);
}
