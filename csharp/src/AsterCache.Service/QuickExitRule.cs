using AsterCache.Windows;

namespace AsterCache.Service;

/// 게임 세션 길이의 분류: 짧은 종료 (연속 횟수에 셈), 정상 세션 (연속 횟수를 되돌림), 그 사이 (세지도 되돌리지도 않음).
public enum QuickExitKind
{
    Quick,
    Between,
    Normal,
}

/// 물러난 실행의 결과 (auto_status.json 의 quick_exit.backed_off 와 알림 문구가 같은 값을 씀).
public static class BackedOffResult
{
    public const string None = "none";
    public const string Exited = "exited";
    public const string Launched = "launched";
}

/// 실행 직후 종료의 판정 규칙 (순수 함수, 모든 게임에 같은 규칙).
public static class QuickExitRule
{
    /// 이 길이 이상이면 정상 세션 (짧은 종료 기준이 이보다 크면 그 기준).
    public static readonly TimeSpan NormalSession = TimeSpan.FromMinutes(5);

    /// 짧은 종료 뒤 이 시간 안에 다시 시작한 실행에서만 서비스가 물러남.
    public static readonly TimeSpan BackOffWindow = TimeSpan.FromMinutes(10);

    /// 기록하는 최근 실행 길이의 개수.
    public const int MaxRuns = 5;

    /// 다시 뜬 프로그램 앞의 실행이 이보다 짧으면 세션을 나누지 않음 (스스로 다시 실행하는 구조의 첫 프로세스를 짧은 종료로 세지 않기 위함).
    public static readonly TimeSpan MinSplitRun = TimeSpan.FromSeconds(5);

    public static bool SplitsSession(TimeSpan previousRun) => previousRun >= MinSplitRun;

    public static QuickExitKind Classify(TimeSpan length, int quickSeconds) =>
        length < TimeSpan.FromSeconds(quickSeconds) ? QuickExitKind.Quick : length >= NormalSession ? QuickExitKind.Normal : QuickExitKind.Between;

    /// 물러날 실행인가: 마지막 짧은 종료 뒤 BackOffWindow 안이고, 이번 연속에서 아직 물러난 적이 없을 때만 (연속당 한 번).
    public static bool ShouldBackOff(DateTimeOffset? lastQuickAt, string? backedOff, DateTimeOffset now) =>
        backedOff is null && lastQuickAt is { } at && now >= at && now - at <= BackOffWindow;

    /// 마지막 정상 실행의 목록에 없는 경로만 (대소문자 무시). 어느 쪽이든 목록이 없으면 null (알 수 없음).
    public static IReadOnlyList<KernelDriver>? NewDrivers(IReadOnlyList<KernelDriver>? lastNormal, IReadOnlyList<KernelDriver>? current)
    {
        if (lastNormal is null || current is null)
        {
            return null;
        }

        HashSet<string> known = new(lastNormal.Select(d => d.Path), StringComparer.OrdinalIgnoreCase);
        return [.. current.Where(d => !known.Contains(d.Path)).DistinctBy(d => d.Path, StringComparer.OrdinalIgnoreCase)];
    }
}
