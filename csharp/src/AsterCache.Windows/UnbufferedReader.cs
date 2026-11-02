using System.ComponentModel;
using System.Runtime.InteropServices;
using AsterCache.Windows.Native;
using Microsoft.Win32.SafeHandles;

namespace AsterCache.Windows;

/// FILE_FLAG_NO_BUFFERING 읽기: 섹터 정렬 버퍼·오프셋·길이로 읽고 요청 범위만 돌려줍니다 (파일 캐시를 거치지 않음).
/// Python bench/replay.UnbufferedReader 와 relayout/mover._read_unbuffered.
public sealed unsafe class UnbufferedReader : IDisposable
{
    private const int Sector = 4096; // 512e 와 4Kn 모두 덮음

    private readonly uint _share;
    private readonly IoPriorityHint? _hint;
    private readonly Dictionary<string, SafeFileHandle> _handles = new();
    private void* _buffer;
    private nuint _capacity;

    /// share: 공유 모드 (기본 읽기만 = Python replay, 삭제·쓰기까지 허용하려면 Win32 ShareAll = 7).
    public UnbufferedReader(uint share = Win32.ShareRead, IoPriorityHint? hint = null)
    {
        _share = share;
        _hint = hint;
    }

    /// 파일 [offset, offset + length) 를 읽어 돌려줍니다 (파일 끝을 넘으면 짧게). 실패하면 Win32Exception 또는 IOException.
    public byte[] Read(string path, long offset, long length)
    {
        if (!_handles.TryGetValue(path, out SafeFileHandle? h))
        {
            h = NativeMethods.CreateFileW(path, Win32.GenericRead, _share, 0, Win32.OpenExisting, Win32.FileFlagNoBuffering, 0);
            if (h.IsInvalid)
            {
                int err = Marshal.GetLastPInvokeError();
                h.Dispose();
                throw new Win32Exception(err);
            }

            _hint?.SetLow(h);
            _handles[path] = h;
        }

        long start = offset - (offset % Sector);
        long span = ((offset + length - start + Sector - 1) / Sector) * Sector;
        EnsureBuffer((nuint)span);
        int got = RandomAccess.Read(h, new Span<byte>(_buffer, checked((int)span)), start);
        long skip = offset - start;
        long take = Math.Max(0, Math.Min(length, got - skip));
        return new Span<byte>((byte*)_buffer + skip, (int)take).ToArray();
    }

    public void Dispose()
    {
        foreach (SafeFileHandle h in _handles.Values)
        {
            h.Dispose();
        }

        _handles.Clear();
        if (_buffer is not null)
        {
            NativeMemory.AlignedFree(_buffer);
            _buffer = null;
            _capacity = 0;
        }
    }

    private void EnsureBuffer(nuint need)
    {
        if (need > _capacity)
        {
            if (_buffer is not null)
            {
                NativeMemory.AlignedFree(_buffer);
                _buffer = null;
                _capacity = 0;
            }

            _buffer = NativeMemory.AlignedAlloc(need, Sector);
            _capacity = need;
        }
    }
}
