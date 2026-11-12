using System.Security.Cryptography;
using AsterCache.Service;

namespace AsterCache.Tray.Update;

/// 내려받은 설치 파일을 두는 폴더 (<데이터 폴더>\updates). 설치 프로그램이 데이터 폴더를 SYSTEM·Administrators 쓰기 전용으로 잠그지만 폴더가 지워졌다 다시 만들어지면 그 잠금이 사라지므로,
/// 준비할 때와 파일을 쓰거나 실행하기 직전에 실제 권한을 다시 검사합니다 (신뢰 밖 쓰기 권한자가 있으면 거부). 재분석 지점 (junction·symlink) 도 거부합니다.
public sealed class UpdateStaging(
    string dataDir, Func<string, bool>? isReparsePoint = null, Func<string, IReadOnlyList<string>>? dirWriters = null, Func<string, IReadOnlyList<string>>? fileWriters = null)
{
    public const string PartialSuffix = ".partial";
    public const string AttemptsFile = "install-attempts.txt";

    private readonly Func<string, bool> _isReparsePoint = isReparsePoint ?? IsReparse;
    private readonly Func<string, IReadOnlyList<string>> _dirWriters = dirWriters ?? AclCheck.UntrustedWriters;
    private readonly Func<string, IReadOnlyList<string>> _fileWriters = fileWriters ?? AclCheck.UntrustedFileWriters;

    public string Dir { get; } = Path.Combine(dataDir, "updates");

    /// 폴더를 만들고 (없으면) 재분석 지점이 아니며 신뢰 밖 쓰기 권한자가 없는지 확인합니다. 데이터 폴더 자체도 검사합니다.
    public void Prepare()
    {
        Directory.CreateDirectory(Dir);
        if (_isReparsePoint(dataDir) || _isReparsePoint(Dir))
        {
            throw new UpdateException("update folder is a reparse point");
        }

        foreach (string dir in new[] { dataDir, Dir })
        {
            if (_dirWriters(dir) is { Count: > 0 } bad)
            {
                throw new UpdateException("update folder can be changed by untrusted accounts: " + string.Join(", ", bad));
            }
        }
    }

    /// 설치 프로그램 이름은 호출하는 쪽이 고정 형식으로만 만든 것이어야 합니다.
    public string PathFor(string installerName)
    {
        if (installerName != Path.GetFileName(installerName))
        {
            throw new UpdateException("installer name must be a plain file name");
        }

        return Path.Combine(Dir, installerName);
    }

    /// 해시를 계산하기 전에 파일이 재분석 지점 (링크) 도 아니고 신뢰 밖 주체가 바꿀 수 있는 파일도 아닌지 확인합니다.
    public string Sha256Hex(string path)
    {
        if (_isReparsePoint(path))
        {
            throw new UpdateException("installer file is a reparse point");
        }

        if (_fileWriters(path) is { Count: > 0 } bad)
        {
            throw new UpdateException("installer file can be changed by untrusted accounts: " + string.Join(", ", bad));
        }

        using FileStream f = new(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return Convert.ToHexStringLower(SHA256.HashData(f));
    }

    /// 부분 파일과 keep 이 아닌 예전 설치 파일을 지웁니다 (실패해도 무시: 설치 프로그램이 아직 쓰는 중일 수 있음).
    public void Clean(string? keep)
    {
        if (!Directory.Exists(Dir))
        {
            return;
        }

        foreach (string file in Directory.EnumerateFiles(Dir))
        {
            string name = Path.GetFileName(file);
            if (name.EndsWith(PartialSuffix, StringComparison.Ordinal) || (name != keep && name.StartsWith("AsterCache-v", StringComparison.Ordinal)))
            {
                TryDelete(file);
            }
        }
    }

    /// 이 버전의 조용한 설치를 시도한 횟수 (기록이 없거나 다른 버전의 기록이면 0). 파일 형식: 첫 줄 버전, 둘째 줄 횟수.
    public int Attempts(string version)
    {
        try
        {
            string[] lines = File.ReadAllLines(Path.Combine(Dir, AttemptsFile));
            return lines.Length >= 2 && lines[0] == version && int.TryParse(lines[1], out int n) && n > 0 ? n : 0;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }

    public void RecordAttempt(string version) => File.WriteAllText(Path.Combine(Dir, AttemptsFile), version + "\n" + (Attempts(version) + 1) + "\n");

    public void ClearAttempts() => TryDelete(Path.Combine(Dir, AttemptsFile));

    public static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // 다음 정리 때 다시 지움
        }
    }

    private static bool IsReparse(string path) => new DirectoryInfo(path).Attributes.HasFlag(FileAttributes.ReparsePoint);
}
