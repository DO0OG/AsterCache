namespace AsterCache.Tracer;

/// 대상 실행 파일 이름과 그 자손 프로세스를 따라갑니다. 종료한 pid 는 빠지므로 pid 재사용은 새 시작 이벤트로 다시 판정합니다.
internal sealed class ProcessTree
{
    private const int KernelImageNameLength = 15; // 커널 프로세스 이벤트의 짧은 이미지 이름 길이
    private readonly HashSet<int> _active = [];
    private readonly string[] _exeNames;

    public ProcessTree(IReadOnlyCollection<string> exeNames)
    {
        _exeNames = exeNames.Select(FileName).ToArray();
        if (_exeNames.Length == 0 || _exeNames.Any(n => n.Length == 0))
        {
            throw new ArgumentException("exe name required", nameof(exeNames));
        }
    }

    public bool IsTarget(int pid) => _active.Contains(pid);

    public bool Start(int pid, int parentPid, string image)
    {
        bool target = _active.Contains(parentPid) || Matches(image);
        if (target)
        {
            _active.Add(pid);
        }
        else
        {
            _active.Remove(pid);
        }

        return target;
    }

    public void Stop(int pid) => _active.Remove(pid);

    private bool Matches(string image)
    {
        string name = FileName(image);
        return name.Length > 0 && _exeNames.Any(exe =>
            exe.Equals(name, StringComparison.OrdinalIgnoreCase)
            || (name.Length == KernelImageNameLength && exe.StartsWith(name, StringComparison.OrdinalIgnoreCase)));
    }

    private static string FileName(string path) => path[(path.LastIndexOfAny(['\\', '/']) + 1)..];
}
