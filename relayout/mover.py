"""Windows volume access for the relayout prototype. Volume ioctls need Administrator; everything else does not.

FSCTL codes are CTL_CODE(FILE_DEVICE_FILE_SYSTEM=9, function, method, FILE_ANY_ACCESS) from winioctl.h.
"""
from __future__ import annotations

import ctypes
import hashlib
import mmap
import re
import struct
import sys

from prefetch.layout import FILE_READ_ATTRIBUTES, INVALID_HANDLE, OPEN_EXISTING, SHARE_ALL, _k32

FSCTL_GET_NTFS_VOLUME_DATA = 0x00090064  # function 25, METHOD_BUFFERED
FSCTL_GET_VOLUME_BITMAP = 0x0009006F     # function 27, METHOD_NEITHER
FSCTL_MOVE_FILE = 0x00090074             # function 29, METHOD_BUFFERED
ERROR_MORE_DATA = 234
GENERIC_READ = 0x80000000
FILE_FLAG_NO_BUFFERING = 0x20000000
SECTOR = 4096  # covers 512e and 4Kn for unbuffered reads
# directory, offline, sparse, reparse point, compressed, encrypted: v1 does not move these
SKIP_ATTRS = {0x10: 'directory', 0x1000: 'offline', 0x200: 'sparse', 0x400: 'reparse', 0x800: 'compressed',
              0x4000: 'encrypted'}


class NotAdmin(RuntimeError):
    """Raised before any volume access when the process is not elevated."""


def is_admin():
    try:
        return bool(ctypes.windll.shell32.IsUserAnAdmin())
    except (AttributeError, OSError):
        return False


def require_admin():
    if not is_admin():
        raise NotAdmin('not_admin')


class MOVE_FILE_DATA(ctypes.Structure):
    """https://learn.microsoft.com/en-us/windows/win32/api/winioctl/ns-winioctl-move_file_data (32 bytes on x64)."""
    _fields_ = [('FileHandle', ctypes.c_void_p), ('StartingVcn', ctypes.c_int64),
                ('StartingLcn', ctypes.c_int64), ('ClusterCount', ctypes.c_uint32)]


class NTFS_VOLUME_DATA_BUFFER(ctypes.Structure):
    """https://learn.microsoft.com/en-us/windows/win32/api/winioctl/ns-winioctl-ntfs_volume_data_buffer (96 bytes)."""
    _fields_ = [('VolumeSerialNumber', ctypes.c_int64), ('NumberSectors', ctypes.c_int64),
                ('TotalClusters', ctypes.c_int64), ('FreeClusters', ctypes.c_int64),
                ('TotalReserved', ctypes.c_int64), ('BytesPerSector', ctypes.c_uint32),
                ('BytesPerCluster', ctypes.c_uint32), ('BytesPerFileRecordSegment', ctypes.c_uint32),
                ('ClustersPerFileRecordSegment', ctypes.c_uint32), ('MftValidDataLength', ctypes.c_int64),
                ('MftStartLcn', ctypes.c_int64), ('Mft2StartLcn', ctypes.c_int64),
                ('MftZoneStart', ctypes.c_int64), ('MftZoneEnd', ctypes.c_int64)]


# VOLUME_BITMAP_BUFFER: https://learn.microsoft.com/en-us/windows/win32/api/winioctl/ns-winioctl-volume_bitmap_buffer
# { LARGE_INTEGER StartingLcn; LARGE_INTEGER BitmapSize; BYTE Buffer[]; } bit i of byte j = LCN StartingLcn+8j+i, 1 = in use.
# Input STARTING_LCN_INPUT_BUFFER { LARGE_INTEGER StartingLcn }.
_TOKENS = re.compile(rb'\x00+|\xff+|[^\x00\xff]', re.S)


class Bitmap:
    """Cluster allocation bitmap (1 = used) covering LCN [start, start + nbits)."""

    def __init__(self, start, nbits, data):
        self.start, self.nbits, self.data = start, nbits, bytes(data)

    def used(self, lcn):
        i = lcn - self.start
        return True if not 0 <= i < self.nbits else bool(self.data[i >> 3] >> (i & 7) & 1)

    def range_free(self, lcn, n):
        return all(not self.used(lcn + k) for k in range(n))

    def free_runs(self):
        """Yield (first_lcn, length) of every free run, ascending."""
        run = None
        end_bit = min(self.nbits, len(self.data) * 8)

        def emit(a, b):
            b = min(b, end_bit)
            return [(self.start + a, b - a)] if b > a else []

        for m in _TOKENS.finditer(self.data):
            s, tok = m.start(), m.group()
            if tok[0] == 0:
                run = s * 8 if run is None else run
            elif tok[0] == 0xFF:
                if run is not None:
                    yield from emit(run, s * 8)
                    run = None
            else:
                for bit in range(8):
                    idx, free = s * 8 + bit, not tok[0] >> bit & 1
                    if free and run is None:
                        run = idx
                    elif not free and run is not None:
                        yield from emit(run, idx)
                        run = None
        if run is not None:
            yield from emit(run, end_bit)


def find_free_run(bitmap, clusters_needed, prefer_low_lcn=True, min_lcn=0):
    """First LCN of a free run >= clusters_needed (lowest run, or highest run when prefer_low_lcn is False); None if none."""
    found = None
    for lcn, n in bitmap.free_runs():
        start = max(lcn, min_lcn)
        if lcn + n - start >= clusters_needed:
            if prefer_low_lcn:
                return start
            found = start
    return found


class Volume:
    """Handle on \\\\.\\X: (Administrator only). Tests subclass and override _ioctl."""

    def __init__(self, handle, k32):
        self._h, self._k = handle, k32
        self.cluster_bytes = self.total_clusters = None
        self.letter = ''

    @classmethod
    def open(cls, letter):
        require_admin()
        k = _k32()
        h = k.CreateFileW('\\\\.\\' + letter.rstrip(':\\') + ':', GENERIC_READ, SHARE_ALL, None, OPEN_EXISTING, 0, None)
        if h in (None, INVALID_HANDLE):
            raise ctypes.WinError(ctypes.get_last_error())
        v = cls(h, k)
        v.letter = letter.rstrip(':\\').upper() + ':'
        v.refresh()
        return v

    def close(self):
        if self._h is not None:
            self._k.CloseHandle(self._h)
            self._h = None

    def _ioctl(self, code, inp, out_len):
        out, got = ctypes.create_string_buffer(out_len), ctypes.c_uint32()
        ok = self._k.DeviceIoControl(self._h, code, inp, len(inp) if inp else 0, out, out_len, ctypes.byref(got), None)
        return bool(ok), 0 if ok else ctypes.get_last_error(), out.raw[:got.value]

    def refresh(self):
        ok, err, out = self._ioctl(FSCTL_GET_NTFS_VOLUME_DATA, None, ctypes.sizeof(NTFS_VOLUME_DATA_BUFFER) + 64)
        if not ok:
            raise ctypes.WinError(err)
        d = NTFS_VOLUME_DATA_BUFFER.from_buffer_copy(out[:ctypes.sizeof(NTFS_VOLUME_DATA_BUFFER)])
        self.cluster_bytes, self.total_clusters = d.BytesPerCluster, d.TotalClusters

    def bitmap(self, chunk_bytes=1 << 20):
        """Whole-volume bitmap, fetched in chunk_bytes pieces (start LCN stays a multiple of 8)."""
        start, data, total = 0, bytearray(), None
        while True:
            ok, err, out = self._ioctl(FSCTL_GET_VOLUME_BITMAP, struct.pack('<q', start), 16 + chunk_bytes)
            if not ok and err != ERROR_MORE_DATA:
                raise ctypes.WinError(err)
            got_start, size = struct.unpack_from('<qq', out)
            payload = out[16:]
            if got_start != start or (not ok and not payload):
                raise OSError('unexpected bitmap chunk')
            total = start + size if total is None else total
            payload = payload[:(size + 7) // 8] if ok else payload
            data += payload
            start += len(payload) * 8
            if ok or start >= total:
                return Bitmap(0, total, data)

    def move(self, path, vcn, lcn, clusters):
        """FSCTL_MOVE_FILE: re-map `clusters` clusters of path from VCN `vcn` to physical `lcn`. Raises OSError."""
        h = self._k.CreateFileW(path, FILE_READ_ATTRIBUTES, SHARE_ALL, None, OPEN_EXISTING, 0, None)
        if h in (None, INVALID_HANDLE):
            raise ctypes.WinError(ctypes.get_last_error())
        try:
            req = MOVE_FILE_DATA(h, vcn, lcn, clusters)
            ok, err, _ = self._ioctl(FSCTL_MOVE_FILE, ctypes.string_at(ctypes.byref(req), ctypes.sizeof(req)), 0)
        finally:
            self._k.CloseHandle(h)
        if not ok:
            raise ctypes.WinError(err)


def file_attrs(path):
    k = _k32()
    k.GetFileAttributesW.argtypes = [ctypes.c_wchar_p]
    k.GetFileAttributesW.restype = ctypes.c_uint32
    a = k.GetFileAttributesW(path)
    if a == 0xFFFFFFFF:
        raise ctypes.WinError(ctypes.get_last_error())
    return a


def skip_reason(path, attr_fn=file_attrs):
    """Why a file must not be moved (compressed/sparse/encrypted/...), or None."""
    try:
        a = attr_fn(path)
    except OSError as e:
        return 'attrs_unreadable: %s' % e
    return next((name for bit, name in SKIP_ATTRS.items() if a & bit), None)


def _read_unbuffered(path, offset, length):
    """Read [offset, offset+length) bypassing the file cache (page-aligned mmap buffer, sector-aligned I/O)."""
    k = _k32()
    k.SetFilePointerEx.argtypes = [ctypes.c_void_p, ctypes.c_int64, ctypes.c_void_p, ctypes.c_uint32]
    k.ReadFile.argtypes = [ctypes.c_void_p, ctypes.c_void_p, ctypes.c_uint32, ctypes.POINTER(ctypes.c_uint32),
                           ctypes.c_void_p]
    h = k.CreateFileW(path, GENERIC_READ, SHARE_ALL, None, OPEN_EXISTING, FILE_FLAG_NO_BUFFERING, None)
    if h in (None, INVALID_HANDLE):
        raise ctypes.WinError(ctypes.get_last_error())
    a = offset // SECTOR * SECTOR
    size = (offset + length - a + SECTOR - 1) // SECTOR * SECTOR
    buf = mmap.mmap(-1, size)
    try:
        view = (ctypes.c_char * size).from_buffer(buf)
        got = ctypes.c_uint32()
        if not k.SetFilePointerEx(h, a, None, 0) or not k.ReadFile(h, ctypes.addressof(view), size, ctypes.byref(got), None):
            raise ctypes.WinError(ctypes.get_last_error())
        del view
        return bytes(buf[offset - a:min(got.value, offset - a + length)])
    finally:
        buf.close()
        k.CloseHandle(h)


def sample_digest(path, size, span=1 << 20, unbuffered=True):
    """SHA-256 over the first, middle and last `span` bytes (and the size). Unbuffered so a corrupted move is not
    hidden by cached pages; falls back to buffered reads if the unbuffered open fails."""
    spans = sorted({(0, min(span, size)), (max(0, size // 2 - span // 2), min(span, size)), (max(0, size - span), min(span, size))})
    h = hashlib.sha256(struct.pack('<q', size))
    for off, n in spans:
        if n <= 0:
            continue
        try:
            if not unbuffered or sys.platform != 'win32':
                raise OSError('buffered')
            h.update(_read_unbuffered(path, off, n))
        except OSError:
            with open(path, 'rb') as f:
                f.seek(off)
                h.update(f.read(n))
    return h.hexdigest()
