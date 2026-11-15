using AsterCache.Windows.Native;

namespace AsterCache.Windows;

/// 적재된 커널 드라이버 하나: 파일 이름과 경로 (운영체제가 주는 모양 그대로, 예: \SystemRoot\System32\drivers\x.sys).
public readonly record struct KernelDriver(string Name, string Path);

/// 시험에서 드라이버 조회의 부분 실패를 재현하기 위한 API.
internal interface IKernelDriverApi
{
    bool Enumerate(Span<nint> bases, out uint needed);

    uint GetBaseName(nint imageBase, Span<char> name);

    uint GetFileName(nint imageBase, Span<char> path);
}

/// 지금 올라와 있는 커널 드라이버 목록 조회 (psapi, 읽기 전용): 프로세스를 열지 않고 드라이버를 건드리지도 않습니다.
/// 게임 시작·짧은 종료 때만 부르는 용도이며, 실패하면 null (호출한 쪽은 단서 없이 계속).
public static class KernelDrivers
{
    private const int PathChars = 520;
    private const int MaxDrivers = 8192;
    private static readonly IKernelDriverApi NativeApi = new NativeDriverApi();

    public static IReadOnlyList<KernelDriver>? Snapshot() => Snapshot(NativeApi);

    internal static IReadOnlyList<KernelDriver>? Snapshot(IKernelDriverApi api)
    {
        try
        {
            for (int attempt = 0; attempt < 2; attempt++)
            {
                if (TrySnapshot(api, out IReadOnlyList<KernelDriver>? drivers))
                {
                    return drivers;
                }
            }
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException or OutOfMemoryException)
        {
            return null;
        }

        return null;
    }

    private static bool TrySnapshot(IKernelDriverApi api, out IReadOnlyList<KernelDriver>? result)
    {
        result = null;
        nint[] bases = new nint[1024];
        if (!api.Enumerate(bases, out uint needed) || needed % (uint)IntPtr.Size != 0)
        {
            return false;
        }

        uint count = needed / (uint)IntPtr.Size;
        if (count > bases.Length)
        {
            if (count > MaxDrivers)
            {
                return false;
            }

            bases = new nint[(int)count];
            if (!api.Enumerate(bases, out needed) || needed % (uint)IntPtr.Size != 0)
            {
                return false;
            }

            count = needed / (uint)IntPtr.Size;
            if (count > bases.Length || count > MaxDrivers)
            {
                return false;
            }
        }

        List<KernelDriver> drivers = new((int)count);
        Span<char> name = stackalloc char[PathChars];
        Span<char> path = stackalloc char[PathChars];
        for (int i = 0; i < count; i++)
        {
            name.Clear();
            path.Clear();
            uint nameLength = api.GetBaseName(bases[i], name);
            uint pathLength = api.GetFileName(bases[i], path);
            if (!CompleteString(name, nameLength) || !CompleteString(path, pathLength))
            {
                return false;
            }

            drivers.Add(new KernelDriver(new string(name[..(int)nameLength]), new string(path[..(int)pathLength])));
        }

        result = drivers.Count == 0 ? null : drivers;
        return true;
    }

    private static bool CompleteString(Span<char> value, uint length) =>
        length > 0 && length < value.Length - 1 && value[(int)length] == '\0';

    private sealed unsafe class NativeDriverApi : IKernelDriverApi
    {
        public bool Enumerate(Span<nint> bases, out uint needed)
        {
            fixed (nint* p = bases)
            {
                return NativeMethods.EnumDeviceDrivers(p, (uint)(bases.Length * sizeof(nint)), out needed);
            }
        }

        public uint GetBaseName(nint imageBase, Span<char> name)
        {
            fixed (char* p = name)
            {
                return NativeMethods.GetDeviceDriverBaseNameW(imageBase, p, (uint)name.Length);
            }
        }

        public uint GetFileName(nint imageBase, Span<char> path)
        {
            fixed (char* p = path)
            {
                return NativeMethods.GetDeviceDriverFileNameW(imageBase, p, (uint)path.Length);
            }
        }
    }
}
