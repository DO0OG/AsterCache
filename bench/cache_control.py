"""Cold-cache conditions. Standby-list purge needs admin; otherwise report failure and use unbuffered proxy."""
import ctypes
from ctypes import wintypes

SystemMemoryListInformation = 80
MemoryFlushModifiedList, MemoryPurgeStandbyList = 3, 4


def is_admin():
    try:
        return bool(ctypes.windll.shell32.IsUserAnAdmin())
    except (AttributeError, OSError):
        return False


class _LUID(ctypes.Structure):
    _fields_ = [('Low', wintypes.DWORD), ('High', wintypes.LONG)]


class _TokenPriv(ctypes.Structure):
    _fields_ = [('Count', wintypes.DWORD), ('Luid', _LUID), ('Attr', wintypes.DWORD)]


def _enable_privilege(name):
    k32 = ctypes.WinDLL('kernel32', use_last_error=True)
    adv = ctypes.WinDLL('advapi32', use_last_error=True)
    k32.GetCurrentProcess.restype = wintypes.HANDLE
    adv.OpenProcessToken.argtypes = [wintypes.HANDLE, wintypes.DWORD, ctypes.POINTER(wintypes.HANDLE)]
    adv.AdjustTokenPrivileges.argtypes = [wintypes.HANDLE, wintypes.BOOL, ctypes.POINTER(_TokenPriv),
                                          wintypes.DWORD, ctypes.c_void_p, ctypes.c_void_p]
    tok = wintypes.HANDLE()
    if not adv.OpenProcessToken(k32.GetCurrentProcess(), 0x28, ctypes.byref(tok)):  # ADJUST|QUERY
        raise ctypes.WinError(ctypes.get_last_error())
    tp = _TokenPriv(1, _LUID(), 2)  # SE_PRIVILEGE_ENABLED
    if not adv.LookupPrivilegeValueW(None, name, ctypes.byref(tp.Luid)):
        raise ctypes.WinError(ctypes.get_last_error())
    ctypes.set_last_error(0)
    ok = adv.AdjustTokenPrivileges(tok, False, ctypes.byref(tp), 0, None, None)
    err = ctypes.get_last_error()
    k32.CloseHandle(tok)
    if not ok or err:  # err 1300 = privilege not held
        raise OSError('AdjustTokenPrivileges failed, winerror=%d' % err)


def _mem_command(cmd):
    c = ctypes.c_int(cmd)
    st = ctypes.WinDLL('ntdll').NtSetSystemInformation(SystemMemoryListInformation, ctypes.byref(c), 4)
    if st != 0:
        raise OSError('NtSetSystemInformation cmd=%d NTSTATUS=0x%08X' % (cmd, st & 0xFFFFFFFF))


def purge_standby():
    """Flush modified list then purge standby list. Never attempted when not admin."""
    if not is_admin():
        return dict(ok=False, is_admin=False, error='not_admin')
    try:
        _enable_privilege('SeProfileSingleProcessPrivilege')
        _mem_command(MemoryFlushModifiedList)
        _mem_command(MemoryPurgeStandbyList)
    except (OSError, AttributeError) as e:
        return dict(ok=False, is_admin=True, error=str(e))
    return dict(ok=True, is_admin=True, error=None)


def make_cold():
    """Return the cold condition actually achieved; replay must be unbuffered when 'unbuffered' is True."""
    p = purge_standby()
    if p['ok']:
        return dict(cold_method='standby_purge', unbuffered=False, purge=p)
    return dict(cold_method='unbuffered_proxy', unbuffered=True, purge=p)


if __name__ == '__main__':
    print(make_cold())
