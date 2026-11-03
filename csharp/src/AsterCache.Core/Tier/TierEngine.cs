using System.ComponentModel;
using AsterCache.Core.Apply;
using AsterCache.Core.Logging;
using AsterCache.Core.Plan;
using AsterCache.Core.Settings;

namespace AsterCache.Core.Tier;

public sealed record TierSkip(string Path, string Reason);

public sealed record TierApplyReport(int Applied, long CopiedBytes, IReadOnlyList<TierSkip> Skipped, string? Stopped);

public sealed record TierRevertReport(int Reverted, int Stale, int Failed, bool Corrupt);

public sealed record TierGameInfo(string Game, string? Root, int Active, int Stale, int Reverted, long ActiveBytes, long CopyBytes, bool Corrupt);

/// SSD 보조 저장의 적용·유지·되돌림·정리. 파일 시스템은 ITierFs 뒤에 있고, 저널 (<journalDir>\<게임>.json) 만 직접 씁니다.
/// 원본 데이터는 어떤 경로에서도 지우지 않습니다: 원본은 같은 폴더의 `<이름>.aster-orig` 로 남고, 지우는 것은 링크 자체와 우리가 만든 사본·임시 파일 (aster tier clean 일 때만 사본) 뿐입니다.
/// 저널이 읽히지 않으면 (손상) 그 게임은 아무 파일도 건드리지 않고 로그만 남기고 건너뜁니다.
public sealed class TierEngine
{
    public const string OrigSuffix = ".aster-orig";
    public const string PartSuffix = ".part";
    private const string CancelledReason = "cancelled";

    /// SSD 여유가 이 아래면 새로 옮기지 않고, 가장 오래된 게임부터 되돌립니다. 16 GiB 와 용량의 2% 중 큰 값.
    public static long MinFreeFor(long totalBytes) => Math.Max(16L << 30, totalBytes / 50);

    private readonly ITierFs _fs;
    private readonly ILog _log;
    private readonly string _dir;
    private readonly Func<DateTimeOffset> _now;
    private readonly HashSet<string> _corruptLogged = new(StringComparer.Ordinal);

    public TierEngine(ITierFs fs, ILog log, string journalDir, Func<DateTimeOffset>? now = null)
    {
        _fs = fs;
        _log = log;
        _dir = journalDir;
        _now = now ?? (() => DateTimeOffset.UtcNow);
    }

    public ITierFs Fs => _fs;

    public string JournalDir => _dir;

    public string JournalPath(string game) => System.IO.Path.Combine(_dir, game + ".json");

    /// 저널이 있는 게임 이름들 (이름순).
    public IReadOnlyList<string> Games() => Directory.Exists(_dir)
        ? [.. Directory.EnumerateFiles(_dir, "*.json").Select(System.IO.Path.GetFileNameWithoutExtension).OfType<string>().Order(StringComparer.Ordinal)]
        : [];

    /// 사본 위치: root\게임\드라이브를 뺀 원래 경로.
    public static string DestPath(string root, string game, string path) =>
        System.IO.Path.Combine(root, game, path[PlanFile.SplitDrive(path).Length..].TrimStart('\\', '/'));

    public static bool IsUnder(string path, string root)
    {
        string r = System.IO.Path.GetFullPath(root).TrimEnd('\\') + "\\";
        return System.IO.Path.GetFullPath(path).StartsWith(r, StringComparison.OrdinalIgnoreCase);
    }

    /// 저널에 살아 있는 (active) 항목이 있는가. 읽을 수 없으면 거짓 (재적용 검사 쪽이 쓰는 가벼운 질문).
    public static bool HasActive(string journalDir, string game)
    {
        try
        {
            string path = System.IO.Path.Combine(journalDir, game + ".json");
            return File.Exists(path) && TierJournal.Load(path).Live.Any();
        }
        catch (TierJournalException)
        {
            return false;
        }
    }

    public (TierJournal? Journal, bool Corrupt) Read(string game)
    {
        string path = JournalPath(game);
        if (!File.Exists(path))
        {
            return (null, false);
        }

        try
        {
            TierJournal j = TierJournal.Load(path);
            return string.Equals(j.Game, game, StringComparison.Ordinal) ? (j, false) : throw new TierJournalException("game name mismatch");
        }
        catch (TierJournalException e)
        {
            if (_corruptLogged.Add(game))
            {
                _log.Warn($"{game}: ssd tier journal unreadable, nothing touched ({e.Message})");
            }

            return (null, true);
        }
    }

    /// 선택한 파일을 SSD 로: 사본 → 크기·표본 SHA 확인 → 저널 기록 → 원본을 `.aster-orig` 로 이름 바꿈 → 링크 → 링크로 크기 확인. 파일마다 실패하면 그 파일만 되돌립니다.
    /// 취소되면 지금 파일만 마무리하고 멈춥니다.
    public TierApplyReport Apply(GameSettings g, string root, int budgetGib, TierSelection selection, CancellationToken ct)
    {
        var skipped = new List<TierSkip>();
        (TierJournal? existing, bool corrupt) = Read(g.Name);
        if (corrupt)
        {
            return new TierApplyReport(0, 0, skipped, "journal_corrupt");
        }

        TierJournal j = existing ?? new TierJournal { Game = g.Name, Root = root };
        if (j.Live.Any() && !Same(j.Root, root))
        {
            return new TierApplyReport(0, 0, skipped, "root_changed");
        }

        if (!j.Live.Any())
        {
            j.AppliedAt = _now();
        }

        (j.Root, j.BudgetGib) = (root, budgetGib);
        int applied = 0;
        long copied = 0;
        string? stopped = null;
        try
        {
            _fs.EnsureRoot(root);
        }
        catch (Exception e) when (Io(e))
        {
            _log.Warn($"{g.Name}: ssd tier folder could not be prepared ({e.GetType().Name})");
            return new TierApplyReport(0, 0, skipped, "root_unavailable");
        }

        long minFree = MinFreeFor(_fs.TotalBytes(root));
        foreach (TierPick pick in selection.Files)
        {
            if (ct.IsCancellationRequested)
            {
                stopped = "cancelled";
                break;
            }

            if (_fs.FreeBytes(root) - pick.Size < minFree)
            {
                stopped = "ssd_free_low";
                break;
            }

            if (ApplyOne(g, root, pick, j, ct) is { } why)
            {
                if (why == CancelledReason)
                {
                    stopped = "cancelled";
                    break;
                }

                skipped.Add(new TierSkip(pick.Path, why));
            }
            else
            {
                applied++;
                copied += pick.Size;
            }
        }

        _log.Info($"{g.Name}: ssd tier applied {applied} file(s), {copied} bytes, {skipped.Count} skipped" + (stopped is null ? string.Empty : $", stopped: {stopped}"));
        return new TierApplyReport(applied, copied, skipped, stopped);
    }

    // 성공이면 null, 아니면 이유 (이 파일은 원래대로).
    private string? ApplyOne(GameSettings g, string root, TierPick pick, TierJournal j, CancellationToken ct)
    {
        string path = pick.Path;
        if (!PlanGuards.Inside(path, g.AllowedRoots, System.IO.Path.GetFullPath))
        {
            return "outside_allowed_root";
        }

        if (TierSelector.Excluded(path))
        {
            return "excluded_type";
        }

        if (HasReparse(path))
        {
            return "reparse_point";
        }

        string orig = path + OrigSuffix;
        string dst = DestPath(root, g.Name, path);
        if (j.Entries.Any(e => e.State != TierState.Reverted && Same(e.Path, path)))
        {
            return "already_in_journal";
        }

        if (_fs.Exists(orig))
        {
            return "orig_name_taken";
        }

        if (!IsUnder(dst, root))
        {
            return "target_outside_root";
        }

        if (HasReparse(dst))
        {
            return "target_reparse_point"; // SYSTEM 이 쓰는 사본 자리에 정션이 심겨 있으면 다른 곳을 쓰게 되므로 건너뜀
        }

        string part = dst + PartSuffix;
        TierEntry? entry = null;
        bool renamed = false;
        try
        {
            FileStamp src = _fs.Stat(path) is { } s && s.Length == pick.Size ? s : throw new IOException("size changed since selection");
            string sha = Sha(path, src.Length);
            if (!(_fs.Stat(dst) is { } old && old.Length == src.Length && old.MtimeTicks == src.MtimeTicks && Sha(dst, old.Length) == sha)) // 되돌린 뒤 남은 사본이 같으면 다시 쓰지 않음
            {
                _fs.Copy(path, part, ct);
                if (_fs.Stat(part) is not { } copy || copy.Length != src.Length || Sha(part, copy.Length) != sha)
                {
                    _fs.DeleteFile(part);
                    return "verify_mismatch";
                }

                _fs.Move(part, dst, true);
            }

            entry = new TierEntry
            {
                Path = path,
                Target = dst,
                Orig = orig,
                Size = src.Length,
                Sha = sha,
                At = _now(),
                MtimeTicks = (_fs.Stat(dst) ?? throw new IOException("copy missing")).MtimeTicks,
            };
            j.Entries.Add(entry);
            j.Save(JournalPath(g.Name)); // 원본 이름을 바꾸기 전에 기록: 중간에 멈춰도 다음 검사가 이름을 되돌림
            _fs.Move(path, orig, false);
            renamed = true;
            _fs.CreateSymlink(path, dst);
            if (_fs.LinkTarget(path) is not { } link || !Same(link, dst) || _fs.Stat(path)?.Length != src.Length)
            {
                throw new IOException("link check failed");
            }

            return null;
        }
        catch (OperationCanceledException)
        {
            Rollback(g.Name, path, orig, part, j, entry, renamed); // 복사 중 취소: .part 를 남기지 않음
            return CancelledReason;
        }
        catch (Exception e) when (Io(e))
        {
            Rollback(g.Name, path, orig, part, j, entry, renamed);
            return $"{e.GetType().Name}: {e.Message}";
        }
    }

    // 이 파일만 되돌립니다 (링크가 있으면 링크만 지우고, 이름이 비었으면 원본 이름을 복구). 다 되돌렸으면 저널 항목도 뺍니다.
    private void Rollback(string game, string path, string orig, string part, TierJournal j, TierEntry? entry, bool renamed)
    {
        try
        {
            if (_fs.Exists(part))
            {
                _fs.DeleteFile(part);
            }

            if (entry is not null)
            {
                if (_fs.LinkTarget(path) is not null)
                {
                    _fs.DeleteLink(path);
                }

                if (renamed && !_fs.Exists(path) && _fs.Exists(orig))
                {
                    _fs.Move(orig, path, false);
                }

                if (_fs.Exists(path) && _fs.LinkTarget(path) is null)
                {
                    j.Entries.Remove(entry);
                    if (j.Entries.Count == 0)
                    {
                        File.Delete(JournalPath(game)); // 저널이 비면 남기지 않음
                    }
                    else
                    {
                        j.Save(JournalPath(game));
                    }
                }
            }
        }
        catch (Exception e) when (Io(e))
        {
            _log.Warn($"{game}: ssd tier rollback incomplete, journal kept, the next check restores the name ({e.GetType().Name})");
        }
    }

    /// 재적용 틱마다: 저널의 active 항목이 그대로인지 봅니다. 사본이 없거나 크기가 다르면 즉시 되돌림, 링크가 일반 파일로 바뀌었거나 사본이 링크를 통해 바뀌었으면 stale 로 표시하고 파일은 그대로.
    public TierGameInfo? Maintain(string game)
    {
        (TierJournal? j, bool corrupt) = Read(game);
        if (j is null)
        {
            return corrupt ? new TierGameInfo(game, null, 0, 0, 0, 0, 0, true) : null;
        }

        bool dirty = false;
        foreach (TierEntry e in j.Live.ToList())
        {
            dirty |= Check(e);
        }

        if (dirty)
        {
            SaveQuietly(j);
        }

        return Info(j);
    }

    private bool Check(TierEntry e)
    {
        string? link = _fs.LinkTarget(e.Path);
        if (link is not null && Same(link, e.Target))
        {
            FileStamp? copy = _fs.Stat(e.Target);
            if (copy is null || copy.Value.Length != e.Size)
            {
                _log.Warn($"{e.Path}: ssd copy missing or its size differs, going back to the original");
                return RevertOne(e);
            }

            if (copy.Value.MtimeTicks == e.MtimeTicks)
            {
                return false;
            }

            _log.Warn($"{e.Path}: ssd copy was changed through the link, left as it is (stale)");
            e.State = TierState.Stale;
            return true;
        }

        if (link is not null || _fs.Exists(e.Path))
        {
            _log.Info($"{e.Path}: link replaced by another file (game or launcher update), original and copy left in place (stale)");
            e.State = TierState.Stale;
            return true;
        }

        if (_fs.Exists(e.Orig)) // 링크가 사라졌고 원본 이름만 남음 (적용 중 멈췄거나 링크를 지움): 이름을 되돌림
        {
            _log.Info($"{e.Path}: link gone, restoring the original name");
            return RevertOne(e);
        }

        _log.Warn($"{e.Path}: link and original are both gone (stale)");
        e.State = TierState.Stale;
        return true;
    }

    /// 게임 하나를 되돌립니다: 링크 삭제 + 이름 복구. 사본은 그대로 두고 저널에 reverted 로 표시합니다.
    public TierRevertReport Revert(string game, string reason)
    {
        (TierJournal? j, bool corrupt) = Read(game);
        if (j is null)
        {
            return new TierRevertReport(0, 0, 0, corrupt);
        }

        int ok = 0, stale = 0, failed = 0;
        List<TierEntry> todo = [.. j.Entries.Where(e => e.State != TierState.Reverted).Reverse()];
        if (todo.Count == 0)
        {
            return new TierRevertReport(0, 0, 0, false); // 할 일이 없으면 로그도 남기지 않음 (꺼진 상태에서 틱마다 불러도 조용)
        }

        foreach (TierEntry e in todo)
        {
            string before = e.State;
            if (!RevertOne(e))
            {
                failed++;
                continue;
            }

            _ = e.State == TierState.Reverted && before != TierState.Stale ? ok++ : stale++;
            SaveQuietly(j);
        }

        _log.Info($"{game}: ssd tier revert ({reason}): {ok} restored, {stale} left alone, {failed} failed");
        return new TierRevertReport(ok, stale, failed, false);
    }

    // 항목 하나. 상태가 바뀌었으면 true (실패해서 그대로면 false, 로그만).
    private bool RevertOne(TierEntry e)
    {
        try
        {
            string? link = _fs.LinkTarget(e.Path);
            bool ours = link is not null && Same(link, e.Target);
            if (e.State == TierState.Stale)
            {
                if (ours && _fs.Stat(e.Target) is not null)
                {
                    Materialize(e); // 사본이 링크를 통해 바뀐 채 남은 경우: 바뀐 데이터를 일반 파일로 되돌려 놓음 (옛 원본은 .aster-orig 로 남음)
                }

                e.State = TierState.Reverted;
                return true;
            }

            if (ours)
            {
                if (!_fs.Exists(e.Orig))
                {
                    _log.Error($"{e.Path}: original name missing, link kept so the name is not lost", null);
                    return false;
                }

                _fs.DeleteLink(e.Path);
                _fs.Move(e.Orig, e.Path, false);
            }
            else if (link is null && !_fs.Exists(e.Path) && _fs.Exists(e.Orig))
            {
                _fs.Move(e.Orig, e.Path, false);
            }
            else
            {
                _log.Info($"{e.Path}: not our link any more, left alone (stale)");
                e.State = TierState.Stale;
                return true;
            }

            e.State = TierState.Reverted;
            return true;
        }
        catch (Exception ex) when (Io(ex))
        {
            _log.Warn($"{e.Path}: revert failed, will retry ({ex.GetType().Name}: {ex.Message})");
            return false;
        }
    }

    private void Materialize(TierEntry e)
    {
        string part = e.Path + PartSuffix;
        _fs.Copy(e.Target, part);
        if (_fs.Stat(part)?.Length != _fs.Stat(e.Target)?.Length)
        {
            _fs.DeleteFile(part);
            throw new IOException("copy back size mismatch");
        }

        _fs.DeleteLink(e.Path);
        _fs.Move(part, e.Path, false);
        _log.Info($"{e.Path}: data written through the link kept as a normal file; the old original stays as {OrigSuffix}");
    }

    /// 사본 정리 (aster tier clean 일 때만): reverted 항목의 사본 중 어떤 링크도 가리키지 않고 다른 항목이 쓰지 않는 것만 지웁니다.
    public (int Files, long Bytes) Clean()
    {
        int files = 0;
        long bytes = 0;
        foreach (string game in Games())
        {
            (TierJournal? j, _) = Read(game);
            if (j is null)
            {
                continue;
            }

            foreach (TierEntry e in j.Entries.Where(e => e.State == TierState.Reverted).ToList())
            {
                if (_fs.LinkTarget(e.Path) is { } link && Same(link, e.Target) || j.Entries.Any(o => o != e && o.State != TierState.Reverted && Same(o.Target, e.Target)))
                {
                    continue;
                }

                try
                {
                    if (_fs.Stat(e.Target) is { } stamp)
                    {
                        _fs.DeleteFile(e.Target);
                        files++;
                        bytes += stamp.Length;
                    }

                    j.Entries.Remove(e);
                }
                catch (Exception ex) when (Io(ex))
                {
                    _log.Warn($"{e.Target}: copy not deleted ({ex.GetType().Name})");
                }
            }

            _fs.RemoveEmptyDirs(j.Root);
            if (j.Entries.Count == 0)
            {
                File.Delete(JournalPath(game));
            }
            else
            {
                SaveQuietly(j);
            }
        }

        return (files, bytes);
    }

    public IReadOnlyList<TierGameInfo> Status() => [.. Games().Select(g => Read(g) is { Journal: { } j } ? Info(j) : new TierGameInfo(g, null, 0, 0, 0, 0, 0, true))];

    /// root 에 사본이 있는 게임 중 가장 먼저 적용한 (active 항목이 있는) 게임, 없으면 null.
    public string? OldestActive(string root) => Games().Select(g => Read(g).Journal).OfType<TierJournal>()
        .Where(j => j.Live.Any() && Same(j.Root, root)).OrderBy(j => j.AppliedAt).Select(j => j.Game).FirstOrDefault();

    private static TierGameInfo Info(TierJournal j) => new(j.Game, j.Root, j.Entries.Count(e => e.State == TierState.Active), j.Entries.Count(e => e.State == TierState.Stale),
        j.Entries.Count(e => e.State == TierState.Reverted), j.ActiveBytes, j.Entries.Sum(e => e.Size), false);

    private void SaveQuietly(TierJournal j)
    {
        try
        {
            j.Save(JournalPath(j.Game));
        }
        catch (Exception e) when (Io(e))
        {
            _log.Warn($"{j.Game}: ssd tier journal not written ({e.GetType().Name})");
        }
    }

    // 파일이나 위쪽 폴더에 링크·정션이 이미 있는가.
    private bool HasReparse(string path)
    {
        if (_fs.IsReparsePoint(path))
        {
            return true;
        }

        for (string? d = System.IO.Path.GetDirectoryName(path); !string.IsNullOrEmpty(d); d = System.IO.Path.GetDirectoryName(d))
        {
            if (_fs.IsReparsePoint(d))
            {
                return true;
            }
        }

        return false;
    }

    private string Sha(string path, long size) => SampleHasher.Compute(path, size, null, _fs.ReadRange);

    private static bool Same(string a, string b) =>
        string.Equals(System.IO.Path.GetFullPath(a).TrimEnd('\\'), System.IO.Path.GetFullPath(b).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);

    private static bool Io(Exception e) => e is IOException or UnauthorizedAccessException or Win32Exception or NotSupportedException;
}
