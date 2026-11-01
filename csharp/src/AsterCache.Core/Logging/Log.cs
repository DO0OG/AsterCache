namespace AsterCache.Core.Logging;

public enum LogLevel { Debug, Info, Warn, Error }

/// 서비스 로그 출력 자리. 시험은 메모리 구현으로 바꿉니다.
public interface ILog
{
    void Write(LogLevel level, string message, Exception? exception = null);
}

/// 파일 로그 (크기 기준 롤링: 이름, 이름.1 … 이름.N). 메시지와 예외 문구에서 사용자 폴더 경로는 %USERPROFILE% 로 가립니다.
public sealed class FileLog : ILog
{
    public const long DefaultMaxBytes = 5L << 20;
    private readonly string _path;
    private readonly long _maxBytes;
    private readonly int _keep;
    private readonly object _gate = new();

    public FileLog(string path, long maxBytes = DefaultMaxBytes, int keep = 3)
    {
        _path = path;
        _maxBytes = maxBytes;
        _keep = keep;
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
    }

    /// 사용자 홈 경로를 가립니다 (대소문자 무시).
    public static string Scrub(string text)
    {
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return home.Length == 0 ? text : text.Replace(home, "%USERPROFILE%", StringComparison.OrdinalIgnoreCase);
    }

    public void Write(LogLevel level, string message, Exception? exception = null)
    {
        string line = $"{DateTimeOffset.Now:O} [{level.ToString().ToUpperInvariant()}] {Scrub(message)}";
        if (exception is not null)
        {
            line += $" ({exception.GetType().Name}: {Scrub(exception.Message)})"; // 스택에는 개인 경로가 들어갈 수 있어 쓰지 않음
        }

        lock (_gate)
        {
            try
            {
                Roll();
                File.AppendAllText(_path, line + Environment.NewLine);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // shortcut: 로그 쓰기 실패는 삼킴 (서비스 동작이 로그보다 중요), 필요하면 이벤트 로그로 올릴 것
            }
        }
    }

    private void Roll()
    {
        if (!File.Exists(_path) || new FileInfo(_path).Length < _maxBytes)
        {
            return;
        }

        for (int i = _keep - 1; i >= 1; i--)
        {
            if (File.Exists($"{_path}.{i}"))
            {
                File.Move($"{_path}.{i}", $"{_path}.{i + 1}", true);
            }
        }

        File.Move(_path, _path + ".1", true);
    }
}

public static class LogExtensions
{
    public static void Info(this ILog log, string message) => log.Write(LogLevel.Info, message);

    public static void Warn(this ILog log, string message) => log.Write(LogLevel.Warn, message);

    public static void Error(this ILog log, string message, Exception? e = null) => log.Write(LogLevel.Error, message, e);
}
