using AsterCache.Core.Logging;
using AsterCache.Core.Plan;
using AsterCache.Core.Settings;
using AsterCache.Windows;

namespace AsterCache.Service;

/// 서비스 시작 때 게임 드라이브별 디스크 종류를 한 줄씩 남깁니다 (로그 전용, 동작은 바꾸지 않음).
public static class DiskKinds
{
    public static void LogAtStart(IEnumerable<GameSettings> games, ILog log, Func<string, DiskDetails>? query = null)
    {
        query ??= DiskInfo.Query;
        foreach (string letter in games.Select(g => PlanFile.SplitDrive(g.OriginRoot).ToUpperInvariant()).Distinct())
        {
            try
            {
                DiskDetails disk = query(letter);
                log.Info($"disk {letter} {DiskInfo.Kind(disk.IncursSeekPenalty, disk.Trim)}" + (disk.Model.Length == 0 ? string.Empty : $" ({disk.Model})"));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
            {
                log.Warn($"disk {letter} query failed");
            }
        }
    }
}
