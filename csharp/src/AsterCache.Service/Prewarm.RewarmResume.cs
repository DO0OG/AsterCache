using System.ComponentModel;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using AsterCache.Core;
using AsterCache.Core.Json;
using AsterCache.Core.Logging;
using AsterCache.Core.Plan;
using AsterCache.Core.Prefetch;
using AsterCache.Core.Settings;
using AsterCache.Prefetch;
using AsterCache.Windows;

namespace AsterCache.Service;

/// Prewarmer 의 끊긴 다시 채우기 (재예열) 이어 읽기: 다른 게임이나 이 게임의 실행으로 다시 채우기가 끊기면 끝까지 읽은 위치를 그 읽기 목록의 지문과 함께 남기고,
/// 다음 다시 채우기가 목록을 확정한 뒤 같은 지문이고 앞부분이 아직 메모리에 있을 때만 남은 부분부터 읽습니다. 하나라도 어긋나면 위치를 버리고 처음부터 읽습니다 (예전 동작).
/// 위치는 상태 파일의 그 게임 항목에 함께 남아 서비스를 다시 시작해도 이어집니다 (부팅 전에 남긴 것은 캐시가 비었으므로 버림).
public sealed partial class Prewarmer
{
    private sealed record RewarmResume(string Key, long Bytes, DateTimeOffset At);

    private readonly Dictionary<string, RewarmResume> _rewarmResume = new(StringComparer.OrdinalIgnoreCase); // 끊긴 다시 채우기의 이어 읽기 위치, _gate 로 보호
    private bool _streamResumed; // 이번 예열 한 회의 스트림이 저장한 위치에서 이어 읽었는가, _run 아래에서만 씀

    // 읽기 목록의 지문: 순서가 있는 (경로, 시작, 길이) 와 그 파일들의 세대 (크기·수정 시각). 목록·파일 교체·창 크기·틈 메우기 예산이 바뀌면 달라집니다.
    private string RewarmListKey(IReadOnlyList<ReadOp> ops)
    {
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);
        foreach (ReadOp op in ops)
        {
            hash.AppendData(Encoding.UTF8.GetBytes($"{op.Path}|{op.Offset}|{op.Length}\n"));
            if (seen.Add(op.Path))
            {
                Fingerprint? stamp = File.Exists(op.Path) ? _env.FileStamp(op.Path) : null;
                hash.AppendData(Encoding.UTF8.GetBytes(stamp is null ? "-\n" : $"{stamp.Size}:{stamp.MtimeNs}\n"));
            }
        }

        return Convert.ToHexString(hash.GetHashAndReset());
    }

    // 다시 채우기 스트림이 읽기 목록을 확정한 직후 (목록은 같은 회의 계획을 재사용): 이 읽기의 지문과, 저장한 위치를 쓸 수 있으면 건너뛸 바이트를 돌려줍니다.
    // 지문을 만들 수 없으면 (모르는 스트리머·계획 없음) 이어 읽지도 위치를 남기지도 않습니다. 취소는 호출한 쪽으로 넘어갑니다.
    private (string? Key, long SkipBytes) BeginRewarmResume(GameSettings game, long maxBytes, CancellationToken stop)
    {
        try
        {
            if (_streamer.PrewarmStreamOps(game, maxBytes, stop) is not { Count: > 0 } ops)
            {
                return (null, 0);
            }

            string key = RewarmListKey(ops);
            long skip = ResolveRewarmResume(game, key, ops, stop);
            _streamResumed |= skip > 0;
            return (key, skip);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or PlanRejectedException or PyValueException or Win32Exception)
        {
            return (null, 0);
        }
    }

    // 저장한 위치가 이 목록의 위치이고 앞부분이 아직 메모리에 있으면 그 바이트, 아니면 위치를 버리고 0. 표본은 읽을 목록과 같은 (스파스를 뺀) 목록의 앞부분에서만 고릅니다.
    private long ResolveRewarmResume(GameSettings game, string key, IReadOnlyList<ReadOp> ops, CancellationToken stop)
    {
        RewarmResume? saved;
        lock (_gate)
        {
            _ = _rewarmResume.TryGetValue(game.Name, out saved);
        }

        if (saved is null)
        {
            return 0;
        }

        string? drop = null;
        if (saved.Key != key || saved.Bytes <= 0 || saved.Bytes >= ops.Sum(op => op.Length))
        {
            drop = "the read list or its files changed";
        }
        else
        {
            ProbeResult? front = ProbeSamplesForGame(game, CacheProbe.PickSamples(PrefetchStreamer.Head(ops, saved.Bytes)), "rewarm resume sample", stop);
            if (front is null)
            {
                if (stop.IsCancellationRequested)
                {
                    return 0; // 취소돼 확인하지 못함: 저장한 위치는 그대로 두고 이번 읽기는 곧 끊김
                }

                drop = "the front part could not be sampled";
            }
            else if (front.Cold)
            {
                drop = "the front part is no longer in memory";
            }
        }

        if (drop is null)
        {
            _log.Info($"{game.Name}: rewarm resumes after the {saved.Bytes >> 20} MiB already read by the cancelled one");
            return saved.Bytes;
        }

        ForgetRewarmResume(game.Name);
        _log.Info($"{game.Name}: cancelled rewarm position of {saved.Bytes >> 20} MiB dropped ({drop}), reading from the start");
        return 0;
    }

    // 스트림이 끝난 때: 끝까지 읽었으면 남길 위치가 없고, 끊겼으면 끝까지 읽은 묶음까지의 위치를 남깁니다. 오류가 있었거나 순서를 바꿨으면 위치가 목록의 앞부분과 같다고 볼 수 없어 남기지 않습니다.
    private void NoteRewarmResume(GameSettings game, string key, long position, bool finished, StreamStats? stats)
    {
        if (finished)
        {
            ForgetRewarmResume(game.Name);
        }
        else if (stats is { Errors: 0 } && position > 0 && !_streamer.WasPrewarmReordered(game))
        {
            lock (_gate)
            {
                _rewarmResume[game.Name] = new RewarmResume(key, position, _now());
            }
        }
    }

    private void ForgetRewarmResume(string game)
    {
        lock (_gate)
        {
            _ = _rewarmResume.Remove(game);
        }
    }

    // 상태 파일 항목에 이어 읽기 위치를 씁니다 (_gate 안에서 부름, 없으면 키를 지움).
    private void WriteRewarmResumeFields(string game, JObject entry)
    {
        if (_rewarmResume.TryGetValue(game, out RewarmResume? saved))
        {
            entry["rewarm_resume"] = new JObject
            {
                { "key", saved.Key },
                { "bytes", saved.Bytes },
                { "at", saved.At.ToString("O", CultureInfo.InvariantCulture) },
            };
        }
        else
        {
            _ = entry.Remove("rewarm_resume");
        }
    }

    // 상태 파일에서 이어 읽기 위치를 되살립니다 (LoadStatus 가 부름). 부팅 전에 남긴 위치는 파일 캐시가 비었으므로 버립니다.
    private void LoadRewarmResume(string game, JObject entry, DateTimeOffset boot)
    {
        if (entry.TryGetValue("rewarm_resume", out object? value) && value is JObject o
            && o.TryGetValue("key", out object? key) && key is string keyText
            && o.TryGetValue("bytes", out object? bytes) && bytes is long position && position > 0
            && o.TryGetValue("at", out object? at) && at is string atText
            && DateTimeOffset.TryParse(atText, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out DateTimeOffset savedAt) && savedAt > boot)
        {
            _rewarmResume[game] = new RewarmResume(keyText, position, savedAt);
        }
    }
}
