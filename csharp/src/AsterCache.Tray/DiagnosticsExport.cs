using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;

namespace AsterCache.Tray;

/// 진단 ZIP 에 넣을 값. DataDir 의 고정된 파일만 읽습니다 (게임 폴더는 읽지 않음). UserName 은 가릴 사용자 이름.
internal sealed record DiagnosticsInput(string DataDir, string TrayLogPath, string ProductVersion, string? ServiceVersion, string OsVersion, string UserName)
{
    public long MaxBytes { get; init; } = DiagnosticsExport.DefaultMaxBytes;

    public int LogLines { get; init; } = DiagnosticsExport.DefaultLogLines;
}

internal sealed record DiagnosticsEntry(string Name, bool Included, string Note);

internal sealed record DiagnosticsResult(bool Ok, string? Error, IReadOnlyList<DiagnosticsEntry> Entries);

/// 지원용 진단 ZIP 을 만듭니다. 파일만 만들고 어디로도 보내지 않습니다. 개인 정보는 가립니다:
/// 사용자 프로필 경로의 이름, 현재 사용자 이름, URL 의 사용자 정보와 비밀 질의 값, 비밀번호·토큰 모양 문자열.
/// 내용 크기 합계는 MaxBytes 이하이며, 읽지 못했거나 크기를 넘은 파일은 건너뛰고 manifest.txt 에 사유를 적습니다.
internal static class DiagnosticsExport
{
    public const long DefaultMaxBytes = 20L << 20;
    public const int DefaultLogLines = 2000;
    private const long ManifestReserve = 64 << 10;
    private const long MaxTailBytes = 8L << 20; // 로그 끝에서 이만큼만 읽음 (줄이 아주 긴 파일에서도 메모리를 묶어 둠)
    private const string Redacted = "<redacted>";
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromSeconds(2);

    // 폴더 이름은 다음 구분자 전까지 (공백 포함, 줄바꿈 제외). 경로 글자는 대소문자를 가리지 않음
    private static readonly Regex UserDir = new(
        @"(?<p>[A-Za-z]:(?:\\|/)+Users(?:\\|/)+)(?:\\u[0-9a-fA-F]{4}|[^\\/""'<>|:*?\r\n]){1,64}", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase, RegexTimeout);

    // JSON 키 이름에 token·secret·password·apikey·authorization 이 들어간 쌍은 값 길이와 무관하게 값을 가림
    private static readonly Regex SecretJsonKey = new(
        @"(?<k>""[^""\r\n]*(?:token|secret|password|passwd|pwd|api_?key|authorization)[^""\r\n]*""\s*:\s*)(?:""(?:[^""\\]|\\.)*""|[^\s,}\]]+)",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase, RegexTimeout);

    private static readonly Regex UrlUserInfo = new(@"(?<s>\b[A-Za-z][A-Za-z0-9+.\-]*://)[^/\s""'<>@]+@", RegexOptions.CultureInvariant, RegexTimeout);

    private static readonly Regex SecretQuery = new(
        @"(?<k>[?&;](?:access_token|id_token|refresh_token|token|api_?key|key|client_secret|secret|password|passwd|pwd|signature|sig|auth|code)=)[^&\s""'<>]+",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase, RegexTimeout);

    private static readonly Regex SecretPair = new(
        @"(?<k>\b(?:password|passwd|pwd|secret|token|api[_-]?key|access[_-]?key|client[_-]?secret|authorization)\b""?\s*[:=]\s*""?)(?:(?:Bearer|Basic)\s+)?[^\s"",;&}\]]+",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase, RegexTimeout);

    private static readonly Regex AuthScheme = new(@"\b(?<s>Bearer|Basic)\s+[A-Za-z0-9._~+/=\-]{16,}", RegexOptions.CultureInvariant, RegexTimeout);

    private static readonly Regex TokenShape = new(
        @"\b(?:gh[pousr]_[A-Za-z0-9]{20,}|github_pat_[A-Za-z0-9_]{20,}|sk-[A-Za-z0-9_\-]{20,}|xox[abprs]-[A-Za-z0-9\-]{10,}|AKIA[0-9A-Z]{16}|eyJ[A-Za-z0-9_\-]{10,}\.[A-Za-z0-9_\-]{10,}\.[A-Za-z0-9_\-]{5,})",
        RegexOptions.CultureInvariant, RegexTimeout);

    // 40 자 이상의 영문·숫자 덩어리 (문자와 숫자가 모두 있는 것): 해시나 키일 수 있어 가림. 진단에는 필요하지 않음
    private static readonly Regex LongMixed = new(
        @"(?<![A-Za-z0-9_\-])(?=[A-Za-z0-9_\-]*[A-Za-z])(?=[A-Za-z0-9_\-]*\d)[A-Za-z0-9_\-]{40,}(?![A-Za-z0-9_\-])", RegexOptions.CultureInvariant, RegexTimeout);

    /// 개인 정보와 자격 증명이 들어갈 수 있는 값을 가립니다.
    public static string Redact(string text, string userName)
    {
        if (userName.Length >= 3) // 너무 짧은 이름은 일반 단어를 망가뜨리므로 경로 가림만 적용. 공백이 든 이름이 통째로 가려지게 경로 규칙보다 먼저 실행
        {
            text = Regex.Replace(text, @"(?<![\p{L}\p{N}])" + Regex.Escape(userName) + @"(?![\p{L}\p{N}])", "<user>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RegexTimeout);
        }

        text = UserDir.Replace(text, "${p}<user>");

        text = UrlUserInfo.Replace(text, "${s}" + Redacted + "@");
        text = SecretQuery.Replace(text, "${k}" + Redacted);
        text = SecretJsonKey.Replace(text, "${k}\"" + Redacted + "\"");
        text = SecretPair.Replace(text, "${k}" + Redacted);
        text = AuthScheme.Replace(text, "${s} " + Redacted);
        text = TokenShape.Replace(text, Redacted);
        return LongMixed.Replace(text, Redacted);
    }

    /// zipPath 에 ZIP 을 만듭니다 (임시 파일에 쓴 뒤 교체). 항목별 결과는 Entries 와 ZIP 안의 manifest.txt 에 같습니다.
    public static DiagnosticsResult Write(string zipPath, DiagnosticsInput input)
    {
        var entries = new List<DiagnosticsEntry>();
        string tmp = zipPath + ".tmp";
        bool done = false;
        try
        {
            using (var file = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
            using (var zip = new ZipArchive(file, ZipArchiveMode.Create))
            {
                long used = 0;
                void Add(string name, string text, string note)
                {
                    byte[] bytes = new UTF8Encoding(false).GetBytes(text);
                    ZipArchiveEntry entry = zip.CreateEntry(name, CompressionLevel.Optimal);
                    using Stream s = entry.Open();
                    s.Write(bytes);
                    used += bytes.Length;
                    entries.Add(new DiagnosticsEntry(name, true, $"included, {bytes.Length} bytes{note}"));
                }

                Add("info.txt", Info(input), string.Empty);
                long Remaining() => input.MaxBytes - ManifestReserve - used;
                foreach (string name in new[] { "config.json", "tray.json", "auto_status.json", "prewarm_status.json", "play_samples.json" })
                {
                    AddFile(name, Path.Combine(input.DataDir, name), isLog: false);
                }

                AddFile("logs/service.log", Path.Combine(input.DataDir, "logs", "service.log"), isLog: true);
                AddFile("logs/tray.log", input.TrayLogPath, isLog: true);

                void AddFile(string name, string path, bool isLog)
                {
                    (string? text, string note) = ReadFor(path, isLog, Remaining(), input);
                    if (text is null)
                    {
                        entries.Add(new DiagnosticsEntry(name, false, note));
                        return;
                    }

                    Add(name, text, note);
                }

                ZipArchiveEntry manifest = zip.CreateEntry("manifest.txt", CompressionLevel.Optimal);
                using var writer = new StreamWriter(manifest.Open(), new UTF8Encoding(false));
                writer.Write(Manifest(entries, input.MaxBytes));
            }

            File.Move(tmp, zipPath, true);
            done = true;
            return new DiagnosticsResult(true, null, entries);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return new DiagnosticsResult(false, e.GetType().Name, entries);
        }
        finally
        {
            if (!done)
            {
                TryDelete(tmp); // 어떤 예외로 끝나도 .tmp 를 남기지 않음
            }
        }
    }

    private static (string? Text, string Note) ReadFor(string path, bool isLog, long remaining, DiagnosticsInput input)
    {
        if (!File.Exists(path))
        {
            return (null, "skipped, missing");
        }

        if (remaining <= 0)
        {
            return (null, "skipped, size limit reached");
        }

        try
        {
            string text;
            string note = string.Empty;
            if (isLog)
            {
                text = Redact(ReadTail(path, input.LogLines), input.UserName);
                note = $", last {input.LogLines} lines at most";
                if (Encoding.UTF8.GetByteCount(text) > remaining)
                {
                    text = FitTail(text, remaining);
                    note += ", cut to size limit";
                }
            }
            else
            {
                long length = new FileInfo(path).Length;
                if (length > remaining)
                {
                    return (null, $"skipped, too large ({length} bytes, {remaining} left)");
                }

                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(fs, Encoding.UTF8);
                text = Redact(reader.ReadToEnd(), input.UserName);
                if (Encoding.UTF8.GetByteCount(text) > remaining)
                {
                    return (null, "skipped, size limit reached");
                }
            }

            return (text, note);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return (null, $"skipped, unreadable ({e.GetType().Name})");
        }
        catch (RegexMatchTimeoutException)
        {
            return (null, "skipped, redaction timed out");
        }
    }

    /// 파일 끝의 maxLines 줄 (쓰는 중인 로그도 읽도록 공유 읽기). 끝 MaxTailBytes 바이트 안에서만 읽습니다.
    private static string ReadTail(string path, int maxLines)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        bool partial = fs.Length > MaxTailBytes;
        if (partial)
        {
            fs.Seek(-MaxTailBytes, SeekOrigin.End);
        }

        using var reader = new StreamReader(fs, Encoding.UTF8);
        if (partial)
        {
            reader.ReadLine(); // 중간에서 시작했으므로 잘린 첫 줄은 버림
        }

        return string.Join(Environment.NewLine, ManageViewModel.ReadTail(reader, maxLines));
    }

    /// 끝쪽 maxBytes 바이트 이하가 되도록 앞을 줄 경계에서 잘라 냅니다.
    private static string FitTail(string text, long maxBytes)
    {
        int start = 0;
        while (start < text.Length && Encoding.UTF8.GetByteCount(text.AsSpan(start)) > maxBytes)
        {
            int next = text.IndexOf('\n', start);
            start = next < 0 ? text.Length : next + 1;
        }

        return text[start..];
    }

    private static string Info(DiagnosticsInput input) => string.Join(
        "\n",
        $"tray: {input.ProductVersion}",
        $"service: {input.ServiceVersion ?? "not installed"}",
        $"os: {input.OsVersion}",
        $"runtime: {Environment.Version.ToString(3)}",
        $"created_utc: {DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture)}",
        "note: user names, URL credentials and token-like strings are masked on a best-effort basis; game folders are not read; nothing is uploaded",
        string.Empty);

    private static string Manifest(IEnumerable<DiagnosticsEntry> entries, long maxBytes) =>
        $"content size limit: {maxBytes} bytes\n" + string.Join("\n", entries.Select(e => $"{e.Name}: {e.Note}")) + "\n";

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // 임시 파일 정리 실패는 원래 오류를 가리지 않음
        }
    }
}
