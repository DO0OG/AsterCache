namespace AsterCache.Service;

/// 게임 업데이트로 치운 트레이스·집계의 보관본 (원시 트레이스 이름 뒤에 .stale 또는 .stale.update 가 붙은 파일) 의 보존 규칙.
/// 이 보관본을 읽는 곳은 서비스 시작 때 트레이스를 되살리는 일 (AutoRelocator.RestoreStaleTraces) 하나이고, 그것은 트레이스가 금지된 게임에서만 합니다.
/// 트레이스를 허용하는 게임은 업데이트 뒤 새 플레이로 다시 학습하므로 보관본을 아무도 읽지 않아, 오래된 것은 지웁니다 (계획 보관본 .stale 은 잔여 재계획이 읽으므로 이 규칙의 대상이 아님).
internal static class TraceArchives
{
    /// 보관본을 남겨 두는 기간 (일). 근거: 업데이트 직후 문제를 살펴볼 시간 (재학습은 며칠 안에 끝남) 으로 둔 값이며 이 PC 에서 재지 않았습니다.
    public const int KeepDays = 14;

    /// rawTracePath (plans\<이름>.trace.json) 곁의 <이름>.trace* 보관본 중 cutoffUtc 보다 오래된 것을 지우고 (지운 파일 수, 바이트) 를 돌려줍니다. 지우지 못한 파일은 건너뜁니다.
    public static (int Files, long Bytes) Prune(string rawTracePath, DateTime cutoffUtc)
    {
        string dir = Path.GetDirectoryName(Path.GetFullPath(rawTracePath)) ?? string.Empty;
        string prefix = Path.GetFileName(rawTracePath);
        prefix = prefix.EndsWith(".json", StringComparison.OrdinalIgnoreCase) ? prefix[..^".json".Length] : prefix; // "<이름>.trace"
        int files = 0;
        long bytes = 0;
        try
        {
            foreach (string path in Directory.Exists(dir) ? Directory.EnumerateFiles(dir, prefix + "*") : [])
            {
                string name = Path.GetFileName(path);
                if (!(name.EndsWith(AutoRelocator.StaleSuffix, StringComparison.OrdinalIgnoreCase) || name.EndsWith(AutoRelocator.UpdateSuffix, StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                try
                {
                    var info = new FileInfo(path);
                    if (info.LastWriteTimeUtc >= cutoffUtc)
                    {
                        continue;
                    }

                    long length = info.Length;
                    info.Delete();
                    (files, bytes) = (files + 1, bytes + length);
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    // 잠긴 파일은 다음 서비스 시작에 다시 시도
                }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // 목록을 못 읽으면 아무것도 지우지 않음
        }

        return (files, bytes);
    }
}
