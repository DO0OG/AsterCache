using System.Collections.Concurrent;
using System.Globalization;
using AsterCache.Core.Discovery;
using AsterCache.Core.Logging;
using AsterCache.Core.Plan;
using AsterCache.Core.Settings;
using AsterCache.Core.Tier;

namespace AsterCache.Service;

/// SSD 보조 저장 (선택 기능, auto.ssd_tier) 의 서비스 쪽: 재배치 틱 안에서 호출됩니다 (같은 스레드라 재배치·재적용과 겹치지 않음).
/// 꺼져 있으면 (기본) 저널 폴더가 비어 있는 한 아무것도 하지 않습니다. 꺼졌거나 게임이 제거됐으면 저널에 남은 링크를 되돌립니다.
/// 켜져 있으면 저널 유지 (링크·사본 확인), SSD 여유 부족 시 가장 오래된 게임 되돌림, 그리고 유휴일 때 재배치가 끝난 (또는 재배치가 꺼진) HDD 게임을 한 번에 하나씩 적용합니다.
public sealed class AutoTier
{
    /// 적용을 시도했는데 옮긴 파일이 없거나 일부만 옮긴 게임을 다시 시도하는 간격.
    public static readonly TimeSpan RetryEvery = TimeSpan.FromHours(6);

    public const string TagPrefix = "ssd tier";

    private readonly AutoEnv _env;
    private readonly AutoStatus _status;
    private readonly ILog _log;
    private readonly BlockerNotes _blockers; // 게임 실행 중이라 건너뛸 때 막고 있는 프로세스를 남김 (사유가 바뀔 때만)
    private readonly TierEngine _engine;
    private readonly IdleTracker _idle = new();
    private readonly ConcurrentDictionary<string, long> _bytes = new(StringComparer.Ordinal); // 게임별 지금 SSD 로 옮겨 둔 바이트 (상태 열 표시용)
    private readonly Dictionary<string, DateTimeOffset> _tried = new(StringComparer.Ordinal); // 틱 스레드에서만 씀
    private readonly HashSet<string> _noted = new(StringComparer.Ordinal); // 틱 스레드에서만 씀: 이미 이유를 남긴 건너뜀
    private readonly Dictionary<string, (string Root, string? Cheat)> _cheat = new(StringComparer.Ordinal); // 틱 스레드에서만 씀: 폴더 이름으로 짐작한 안티치트 (게임 루트가 바뀌면 무효)
    private readonly HashSet<string> _deferNoted = new(StringComparer.Ordinal); // 틱 스레드에서만 씀: 되돌림을 미룬다고 이미 남긴 게임
    private string? _rootWhy; // 틱 스레드에서만 씀

    public AutoTier(AutoEnv env, AutoStatus status, ILog log, TierEngine engine)
    {
        _env = env;
        _status = status;
        _log = log;
        _blockers = new BlockerNotes(env, log);
        _engine = engine;
    }

    /// 상태 열에 붙일 표시 ("ssd tier 2.5 GB"), 옮겨 둔 것이 없으면 null.
    public string? Tag(string game) =>
        _bytes.TryGetValue(game, out long b) && b > 0 ? $"{TagPrefix} {(b / (double)(1L << 30)).ToString("0.0", CultureInfo.InvariantCulture)} GB" : null;

    /// 지금 이 게임의 링크가 하나라도 살아 있는가 (재배치 전에 되돌릴지 정하는 데 씀).
    public bool IsActive(string game) => TierEngine.HasActive(_engine.JournalDir, game);

    /// 게임 하나의 링크를 되돌립니다 (업데이트 감지, 재배치 시작 전). 되돌린 뒤 다시 시도할 수 있게 시도 기록도 지웁니다.
    public void RevertGame(string game, string reason)
    {
        if (IsActive(game))
        {
            _engine.Revert(game, reason);
        }

        _tried.Remove(game);
        RefreshBytes(game);
    }

    public void Tick(AsterSettings s, bool relocateOn, ReapplyGate gate, CancellationToken ct, Func<GameSettings, bool>? heavyWorkAllowed = null)
    {
        SsdTierSettings t = s.Auto.SsdTier;
        bool on = s.Auto.Enabled && t.Enabled;
        IReadOnlyList<string> journals = _engine.Games();
        if (!on && journals.Count == 0)
        {
            return; // 기본: 아무것도 건드리지 않음
        }

        foreach (string name in journals)
        {
            GameSettings? g = s.Games.FirstOrDefault(x => x.Name == name);
            if (g is not null && heavyWorkAllowed is not null && !heavyWorkAllowed(g))
            {
                continue;
            }

            if (!on || g is null || TierBlocked(g.AntiCheat ?? FreshCheat(g)))
            {
                string why = !on ? "disabled" : g is null ? "game removed" : "anti-cheat";
                if (!RevertDeferred(s, name, g, why))
                {
                    _ = _engine.Revert(name, why);
                    _tried.Remove(name);
                    RefreshBytes(name);
                }
            }
        }

        if (!on)
        {
            return;
        }

        TierRootChoice choice = TierRoot.Resolve(t, _env.SystemDrive, _engine.Fs.FixedDrives(), letter => _env.IsHdd(letter), _engine.Fs.FreeBytes);
        if (choice.Root is not { } root)
        {
            NoteRootWhy(choice.Why);
            return;
        }

        _rootWhy = null;
        foreach (GameSettings g in s.Games.Where(g => (heavyWorkAllowed?.Invoke(g) ?? true) && !AnyGameRunning([g])))
        {
            Maintain(g, root, t, () => RevertDeferred(s, g.Name, g, "ssd_tier settings changed"));
        }

        long minFree = TierEngine.MinFreeFor(_engine.Fs.TotalBytes(root));
        if (_engine.Fs.FreeBytes(root) < minFree)
        {
            if (_engine.OldestActive(root) is { } oldest && !RevertDeferred(s, oldest, s.Games.FirstOrDefault(x => x.Name == oldest), "ssd free space low"))
            {
                _log.Warn($"{oldest}: ssd tier folder has less than {minFree >> 30} GiB free, reverting the oldest game (copies stay until `aster tier clean`)");
                _ = _engine.Revert(oldest, "ssd free space low");
                _tried.Remove(oldest);
                RefreshBytes(oldest);
            }

            return;
        }

        ApplyNext(s, relocateOn, root, t, gate, ct, heavyWorkAllowed);
    }

    private static bool TierBlocked(string? cheat) => Capabilities.Decide(CapabilityFeature.Tier, cheat).Outcome != CapabilityOutcome.Allow; // 정책표 결정 (안티치트가 있으면 종류와 상관없이 거절)

    // 적용 뒤 생긴 마커도 잡도록 저장값이 없으면 폴더를 다시 봄 (캐시된 "없음"에 가리지 않음). 모름은 캐시하지 않고 옛 캐시를 지움 (신규 적용 때 다시 봄).
    private string? FreshCheat(GameSettings g)
    {
        string? cheat = _env.AntiCheatOf(g.OriginRoot);
        if (cheat == AutoEnv.AntiCheatUnknown)
        {
            _cheat.Remove(g.Name);
        }
        else
        {
            _cheat[g.Name] = (g.OriginRoot, cheat);
        }

        return cheat;
    }

    // 링크·사본 확인. 설정(예산·폴더)이 저널과 다르면 (사용자가 바꿈) 되돌려서 다음에 새 설정으로 다시 적용합니다.
    private void Maintain(GameSettings g, string root, SsdTierSettings t, Func<bool> revertDeferred)
    {
        TierGameInfo? info = _engine.Maintain(g.Name);
        if (info is { Corrupt: false, Active: > 0 } && _engine.Read(g.Name).Journal is { } j && (j.BudgetGib != t.BudgetGib || !string.Equals(j.Root, root, StringComparison.OrdinalIgnoreCase)) && !revertDeferred())
        {
            _ = _engine.Revert(g.Name, "ssd_tier settings changed");
            _tried.Remove(g.Name);
        }

        RefreshBytes(g.Name);
    }

    private void ApplyNext(AsterSettings s, bool relocateOn, string root, SsdTierSettings t, ReapplyGate gate, CancellationToken ct, Func<GameSettings, bool>? heavyWorkAllowed)
    {
        GameSettings? next = s.Games.FirstOrDefault(g => (heavyWorkAllowed?.Invoke(g) ?? true) && Eligible(g, relocateOn));
        if (next is null || !_idle.Sample(_env.Now(), BusyReason(s, next) is null, s.Auto.IdleWindow))
        {
            return;
        }

        string? tracePath = AutoTracer.PlanTracePath(next, _env.FileExists);
        _tried[next.Name] = _env.Now();
        _idle.Reset(); // 다음 게임도 다시 유휴를 기다림
        if (tracePath is null)
        {
            return;
        }

        try
        {
            TierSelection pick = TierSelector.Select(_env.LoadTrace(tracePath, next.OriginRoot), (long)t.BudgetGib << 30, next.AllowedRoots, p => _engine.Fs.Stat(p)?.Length);
            _log.Info($"{next.Name}: ssd tier picked {pick.Files.Count} file(s), {pick.MovedBytes} bytes, covering {pick.CoveredHotBytes} of {pick.TotalHotBytes} hot bytes");
            if (pick.Files.Count > 0)
            {
                TierApplyReport rep = gate.Run(ct, token => _engine.Apply(next, root, t.BudgetGib, pick, token), () => SameDiskRunning(s, next)); // 진입 게이트와 같게 같은 물리 디스크의 등록 게임 전체를 봄
                foreach (TierSkip skip in rep.Skipped.Take(5))
                {
                    _log.Info($"{next.Name}: ssd tier skipped {skip.Path} ({skip.Reason})");
                }
            }
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            _log.Error($"{next.Name}: ssd tier failed", e);
        }

        RefreshBytes(next.Name);
    }

    // 등록 exe 가 도는 게임이 있는지 (AutoRelocator.AnyGameRunning 과 같은 판정).
    private bool AnyGameRunning(IReadOnlyList<GameSettings> games)
    {
        string key = string.Join(",", games.Select(x => x.Name));
        if (_env.AnyRunning([.. games.SelectMany(x => x.ExeNames).Distinct(StringComparer.OrdinalIgnoreCase)]))
        {
            _blockers.Note(key, "ssd tier skipped", games);
            return true;
        }

        _blockers.Clear(key);
        return false;
    }

    private bool SameDiskRunning(AsterSettings s, GameSettings g) => AnyGameRunning([.. AutoRelocator.SameDriveGames(s.Games, [g], _env.SameDisk).Concat([g])]);

    // 게임이 안 돌고, HDD (시스템 볼륨·SSD 아님) 이고, 안티치트가 없고, 재배치가 끝났거나 (재배치가 꺼졌으면 해당 없음) 아직 링크가 없고 최근에 시도하지 않은 게임.
    private bool Eligible(GameSettings g, bool relocateOn)
    {
        if (AnyGameRunning([g]) || !_env.Capabilities(g.OriginRoot).CanTier || _engine.Read(g.Name) is { Journal: { } j } && j.Live.Any() || _engine.Read(g.Name).Corrupt
            || _tried.TryGetValue(g.Name, out DateTimeOffset at) && _env.Now() - at < RetryEvery
            || relocateOn && _status.Get(g.Name)?.RelocatedAt is null)
        {
            return false;
        }

        string? cheat = g.AntiCheat; // 등록된 값은 캐시보다 먼저 (설정이 나중에 바뀌어도 바로 반영)
        if (cheat is null)
        {
            if (_cheat.TryGetValue(g.Name, out var known) && known.Root == g.OriginRoot)
            {
                cheat = known.Cheat;
            }
            else
            {
                cheat = _env.AntiCheatOf(g.OriginRoot);
                if (cheat == AutoEnv.AntiCheatUnknown)
                {
                    return false; // 탐지 실패는 모름: 티어 거절, 캐시하지 않음 (다음 주기에 다시 봄)
                }

                _cheat[g.Name] = (g.OriginRoot, cheat);
            }
        }

        if (TierBlocked(cheat))
        {
            if (_noted.Add(g.Name))
            {
                _log.Info($"{g.Name}: ssd tier skipped, anti-cheat found ({cheat})");
            }

            return false;
        }

        return true;
    }

    // 유휴가 아닌 이유 (유휴면 null): 대상 게임과 같은 드라이브의 등록 게임이 돌거나, 배터리이거나, 대상 드라이브에 디스크 활동이 있음.
    private string? BusyReason(AsterSettings s, GameSettings g)
    {
        if (SameDiskRunning(s, g))
        {
            return "game running";
        }

        if (!_env.PowerOk())
        {
            return "on battery";
        }

        return _env.DiskBytesPerSecond(PlanFile.SplitDrive(g.OriginRoot).ToUpperInvariant()) is not { } bps ? DiskActivity.StatusText : bps >= DiskActivity.BusyBytesPerSecond ? "disk busy" : null;
    }

    // 링크를 되돌리면 링크로 열린 파일의 이후 쓰기가 유실될 수 있으므로, 같은 물리 디스크의 등록 게임 (그 게임 자신 포함) 이 돌면 미룹니다 (사유는 게임마다 한 번만 로그).
    private bool RevertDeferred(AsterSettings s, string game, GameSettings? g, string reason)
    {
        if (!IsActive(game))
        {
            return false;
        }

        List<string> roots = [.. (g is null ? [] : new[] { g.OriginRoot }).Concat(_engine.Read(game).Journal?.Entries.Select(e => e.Path) ?? [])];
        List<GameSettings> games = [.. s.Games.Where(x => roots.Any(r => _env.SameDisk(r, x.OriginRoot))).Concat(g is null ? [] : [g])];
        if (!AnyGameRunning(games))
        {
            _deferNoted.Remove(game);
            return false;
        }

        if (_deferNoted.Add(game))
        {
            _log.Info($"{game}: ssd tier revert deferred ({reason}), a game on the same disk is running");
        }

        return true;
    }

    private void RefreshBytes(string game)
    {
        long bytes = _engine.Read(game).Journal?.ActiveBytes ?? 0;
        _bytes[game] = bytes;
    }

    private void NoteRootWhy(string? why)
    {
        if (why is not null && why != _rootWhy)
        {
            _rootWhy = why;
            _log.Warn($"ssd tier skipped: {why}");
        }
    }
}
