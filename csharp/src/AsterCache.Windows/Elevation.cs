using AsterCache.Core;
using AsterCache.Windows.Native;

namespace AsterCache.Windows;

/// 관리자(승격된 토큰) 여부. Python mover.is_admin / require_admin.
public static class Elevation
{
    public static bool IsAdmin()
    {
        try
        {
            return NativeMethods.IsUserAnAdmin();
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
        {
            return false;
        }
    }

    /// 볼륨에 손대기 전에 부릅니다. 승격되지 않았으면 NotAdminException.
    public static void RequireAdmin(Func<bool>? isAdmin = null)
    {
        if (!(isAdmin ?? IsAdmin)())
        {
            throw new NotAdminException();
        }
    }
}
