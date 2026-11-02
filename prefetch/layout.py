"""Physical file layout on NTFS via FSCTL_GET_RETRIEVAL_POINTERS (no admin needed).

Output buffer layout (Microsoft Learn, RETRIEVAL_POINTERS_BUFFER, winioctl.h):
  DWORD ExtentCount; (4 bytes padding) LARGE_INTEGER StartingVcn;
  struct { LARGE_INTEGER NextVcn; LARGE_INTEGER Lcn; } Extents[ExtentCount];
Extent i covers VCNs [prev NextVcn (first: StartingVcn), NextVcn); Lcn -1 = sparse hole.
Input: STARTING_VCN_INPUT_BUFFER { LARGE_INTEGER StartingVcn }.
"""
import ctypes
import os
import struct
import sys

FSCTL_GET_RETRIEVAL_POINTERS = 0x00090073
FILE_READ_ATTRIBUTES = 0x80
SHARE_ALL = 7  # READ|WRITE|DELETE
OPEN_EXISTING = 3
ERROR_MORE_DATA = 234
INVALID_HANDLE = ctypes.c_void_p(-1).value
_HEADER = 16  # ExtentCount + padding + StartingVcn
_MAX_CHUNK_EXTENTS = 4096


def drive_root(path):
    r"""A bare drive ('D:') means "current directory on D:" to the OS; as a root it is meant to be the drive root.
    Intended: the device form \\.\D: also resolves to D:\ (a device-style drive root counts as a root)."""
    path = os.fspath(path)
    drive, rest = os.path.splitdrive(path)
    return path + os.sep if drive and not rest else path


def _k32():
    k = ctypes.WinDLL('kernel32', use_last_error=True)
    k.CreateFileW.restype = ctypes.c_void_p
    k.CreateFileW.argtypes = [ctypes.c_wchar_p, ctypes.c_uint32, ctypes.c_uint32, ctypes.c_void_p,
                              ctypes.c_uint32, ctypes.c_uint32, ctypes.c_void_p]
    k.CloseHandle.argtypes = [ctypes.c_void_p]
    k.DeviceIoControl.argtypes = [ctypes.c_void_p, ctypes.c_uint32, ctypes.c_void_p, ctypes.c_uint32,
                                  ctypes.c_void_p, ctypes.c_uint32, ctypes.POINTER(ctypes.c_uint32),
                                  ctypes.c_void_p]
    k.GetVolumePathNameW.argtypes = [ctypes.c_wchar_p, ctypes.c_wchar_p, ctypes.c_uint32]
    k.GetDiskFreeSpaceW.argtypes = [ctypes.c_wchar_p] + [ctypes.POINTER(ctypes.c_uint32)] * 4
    return k


def cluster_size(path):
    """Bytes per cluster of the volume holding path, or None."""
    if sys.platform != 'win32':
        return None
    try:
        k = _k32()
        root = ctypes.create_unicode_buffer(1024)
        if not k.GetVolumePathNameW(os.path.abspath(path), root, 1024):
            return None
        spc, bps, _, _ = (ctypes.c_uint32() for _ in range(4))
        if not k.GetDiskFreeSpaceW(root.value, ctypes.byref(spc), ctypes.byref(bps),
                                   ctypes.byref(ctypes.c_uint32()), ctypes.byref(ctypes.c_uint32())):
            return None
        return spc.value * bps.value
    except (OSError, ValueError, AttributeError):
        return None


def file_extents(path):
    """[(vcn_start, lcn_start, cluster_count)] in VCN order, or None on any failure/sparse hole.
    [] means no on-disk extents (e.g. data resident in the MFT or empty file)."""
    if sys.platform != 'win32':
        return None
    try:
        k = _k32()
        h = k.CreateFileW(os.path.abspath(path), FILE_READ_ATTRIBUTES, SHARE_ALL, None, OPEN_EXISTING, 0, None)
        if h in (None, INVALID_HANDLE):
            return None
        try:
            return _read_extents(k, h)
        finally:
            k.CloseHandle(h)
    except (OSError, ValueError, AttributeError, struct.error):
        return None


def _read_extents(k, h):
    out, start = [], 0
    buf = ctypes.create_string_buffer(_HEADER + 16 * _MAX_CHUNK_EXTENTS)
    for _ in range(100000):  # bound the ERROR_MORE_DATA loop
        inp = struct.pack('<q', start)
        got = ctypes.c_uint32()
        ok = k.DeviceIoControl(h, FSCTL_GET_RETRIEVAL_POINTERS, inp, 8, buf, len(buf), ctypes.byref(got), None)
        if not ok and ctypes.get_last_error() != ERROR_MORE_DATA:
            return None
        count, vcn = struct.unpack_from('<Iq', buf, 0)[0], struct.unpack_from('<q', buf, 8)[0]
        for i in range(count):
            nxt, lcn = struct.unpack_from('<qq', buf, _HEADER + 16 * i)
            if lcn < 0:
                return None  # sparse/compressed hole: no usable physical order
            out.append((vcn, lcn, nxt - vcn))
            vcn = nxt
        if ok:
            return out
        if count == 0:
            return None
        start = vcn
    return None


def physical_key(extents, offset, cluster_bytes):
    """LCN of the cluster holding file byte offset, or None if unknown/not on disk."""
    if not extents or not cluster_bytes:
        return None
    vcn = offset // cluster_bytes
    for v, lcn, n in extents:
        if v <= vcn < v + n:
            return lcn + (vcn - v)
    return None
