using AsterCache.Core.Prefetch;
using AsterCache.Core.Profiles;
using AsterCache.Windows;

namespace AsterCache.Prefetch;

/// 기록 (trace) 이 없는 게임의 예열 계획: 읽을 파일 목록 (ReadOp, 파일마다 하나) 과 이름표, 읽을 것이 없으면 이유 (Skip).
/// Label: "full" (폴더 전체) 또는 "profile Unity" 처럼 "profile <엔진>[+<엔진>]".
public sealed record ColdPlan(string Label, IReadOnlyList<ReadOp> Ops, long Bytes, string? Skip)
{
    public static ColdPlan None(string reason) => new(string.Empty, [], 0, reason);
}

/// 설치 폴더만 보고 만드는 예열 계획과 그 읽기. 파일은 읽기 전용으로만 열고, 연결 지점 (junction·symlink) 은 따라가지 않습니다.
public static class FolderWarm
{
    /// 통째 예열 한 번에 읽는 묶음 크기 (파일 경계에서 끊음): 게임이 뜨면 이 묶음이 끝나는 곳에서 멈춥니다.
    public const long BatchBytes = 64L << 20;

    public const int MaxDepth = 12;
    public const int MaxEntries = 1_000_000;

    /// 엔진 프로필이 하나도 안 맞을 때의 이유 (Prewarmer 가 이 값을 보고 폴더 mtime 키로 판정을 기억합니다).
    public const string NoEngineProfile = "no engine profile matched";

    /// fullCapBytes 가 0 보다 크고 폴더 합이 그 이하면 폴더 전체, 아니면 profileBudgetBytes 가 0 보다 크면 엔진 프로필. 합 계산은 상한을 넘는 즉시 멈춥니다.
    /// 폴더 전체는 파일의 물리 배치 (layout, 기본 FileLayout.Get: 파일마다 FSCTL 한 번) 순서로 읽습니다: HDD 에서 탐색을 줄임. 배치를 모르는 파일은 맨 뒤.
    public static ColdPlan Plan(string root, IReadOnlyCollection<string> exeNames, long fullCapBytes, long profileBudgetBytes, EngineProfiles profiles, CancellationToken ct = default,
        Func<string, FileLayoutInfo?>? layout = null)
    {
        if (!Directory.Exists(root))
        {
            throw new DirectoryNotFoundException("install folder not found");
        }

        if (fullCapBytes > 0 && Enumerate(root, fullCapBytes, ct) is { } all)
        {
            (List<ReadOp> ops, _) = Scheduler.Schedule(all.Where(f => f.Size > 0).Select(f => new ReadRange(f.Full, 0, f.Size)), "physical", null, 0,
                p => { ct.ThrowIfCancellationRequested(); return (layout ?? FileLayout.Get)(p); });
            return ops.Count == 0 ? ColdPlan.None("folder has no files") : new ColdPlan("full", ops, ops.Sum(o => o.Length), null);
        }

        if (profileBudgetBytes <= 0)
        {
            return ColdPlan.None("folder is larger than the full-prewarm limit");
        }

        List<Entry> files = Enumerate(root, long.MaxValue, ct)!;
        ProfileSelection pick = profiles.Select([.. files.Select(f => (f.Rel, f.Size))], ExeDir(files, exeNames), profileBudgetBytes);
        if (pick.Engines.Count == 0)
        {
            return ColdPlan.None(NoEngineProfile);
        }

        Dictionary<string, Entry> byRel = files.ToDictionary(f => f.Rel, StringComparer.OrdinalIgnoreCase);
        List<ReadOp> picked = [.. pick.Picks.Select(p => new ReadOp(byRel[p.Path].Full, p.Offset, p.Bytes))];
        return picked.Count == 0 ? ColdPlan.None("engine profile matched no files") : new ColdPlan("profile " + string.Join('+', pick.Engines), picked, picked.Sum(o => o.Length), null);
    }

    /// ops 를 묶음 (BatchBytes 이상이 모일 때까지, 파일 하나는 쪼개지 않음) 으로 읽습니다. ct 가 취소되면 다음 묶음부터 시작하지 않고, 읽은 만큼의 통계와 끝까지 읽었는지(complete) 를 돌려줍니다.
    public static (StreamStats Stats, bool Complete) Read(IReadOnlyList<ReadOp> ops, CancellationToken ct, Func<IReadOnlyList<ReadOp>, StreamStats>? read = null)
    {
        read ??= batch => ZoneStreamer.Run(batch, StreamMethod.Pvm, ct: ct);
        long bytes = 0;
        long requests = 0;
        long errors = 0;
        double seconds = 0;
        long hint = 0;
        long requested = 0;
        bool aborted = false;
        int at = 0;
        while (at < ops.Count)
        {
            if (ct.IsCancellationRequested)
            {
                return (new StreamStats(bytes, requests, errors, seconds, true, requested, hint), false);
            }

            int from = at;
            long size = 0;
            while (at < ops.Count && size < BatchBytes)
            {
                size += ops[at++].Length;
            }

            ReadOp[] batch = [.. ops.Skip(from).Take(at - from)];
            StreamStats s = read(batch);
            (bytes, requests, errors, seconds) = (bytes + s.Bytes, requests + s.Requests, errors + s.Errors, seconds + s.Seconds);
            (hint, requested, aborted) = (hint + s.HintAcceptedBytes, requested + s.RequestedBytes, aborted || s.Aborted);
            if (ct.IsCancellationRequested && s.Bytes < batch.Sum(o => o.Length))
            {
                return (new StreamStats(bytes, requests, errors, seconds, true, requested, hint), false); // 묶음 도중 멈춤 (마지막 묶음이어도 끝까지 읽은 것이 아님)
            }
        }

        return (new StreamStats(bytes, requests, errors, seconds, aborted, requested, hint), true);
    }

    private sealed record Entry(string Rel, string Full, long Size);

    // 순서: 폴더마다 파일 (작은 것부터, 같으면 이름순) → 하위 폴더 (이름순). 연결 지점은 건너뜀. 합이 cap 을 넘으면 null (넘는 즉시 열거를 멈춤).
    private static List<Entry>? Enumerate(string root, long cap, CancellationToken ct)
    {
        var result = new List<Entry>();
        long total = 0;
        return Walk(new DirectoryInfo(root), string.Empty, 0) ? result : null;

        bool Walk(DirectoryInfo dir, string rel, int depth)
        {
            ct.ThrowIfCancellationRequested();
            FileSystemInfo[] items;
            try
            {
                items = dir.GetFileSystemInfos();
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                return true; // 읽을 수 없는 폴더는 건너뜀
            }

            foreach (FileInfo f in items.OfType<FileInfo>().Where(f => (f.Attributes & FileAttributes.ReparsePoint) == 0)
                .OrderBy(f => f.Length).ThenBy(f => f.Name, StringComparer.OrdinalIgnoreCase))
            {
                total += f.Length;
                if (total > cap || result.Count >= MaxEntries)
                {
                    return false;
                }

                result.Add(new Entry(rel + f.Name, f.FullName, f.Length));
            }

            if (depth >= MaxDepth)
            {
                return true;
            }

            foreach (DirectoryInfo d in items.OfType<DirectoryInfo>().Where(d => (d.Attributes & FileAttributes.ReparsePoint) == 0).OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase))
            {
                if (!Walk(d, rel + d.Name + "/", depth + 1))
                {
                    return false;
                }
            }

            return true;
        }
    }

    // 게임 exe 가 있는 폴더 (설치 폴더 기준, 가장 얕은 것), 못 찾으면 null.
    private static string? ExeDir(List<Entry> files, IReadOnlyCollection<string> exeNames)
    {
        Entry? exe = files.Where(f => exeNames.Contains(Path.GetFileName(f.Rel), StringComparer.OrdinalIgnoreCase))
            .OrderBy(f => f.Rel.Count(c => c == '/')).ThenBy(f => f.Rel, StringComparer.OrdinalIgnoreCase).FirstOrDefault();
        if (exe is null)
        {
            return null;
        }

        int slash = exe.Rel.LastIndexOf('/');
        return slash < 0 ? string.Empty : exe.Rel[..slash];
    }
}
