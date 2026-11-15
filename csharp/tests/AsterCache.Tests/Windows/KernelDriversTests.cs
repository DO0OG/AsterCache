using AsterCache.Windows;
using Xunit;

namespace AsterCache.Tests.Windows;

public sealed class KernelDriversTests
{
    [Fact]
    public void PartialDriverLookupRetriesAndReturnsOnlyACompleteList()
    {
        var api = new FakeApi { NameFailuresRemaining = 1 };

        IReadOnlyList<KernelDriver> drivers = Assert.IsAssignableFrom<IReadOnlyList<KernelDriver>>(KernelDrivers.Snapshot(api));

        Assert.Equal(2, api.EnumerateCalls);
        Assert.Equal("driver1.sys", Assert.Single(drivers).Name);
    }

    [Fact]
    public void GrowingListAndTruncatedStringsAreUnknownAfterOneRetry()
    {
        var growing = new FakeApi { EnumerationCounts = [1025, 1026, 1025, 1026] };
        Assert.Null(KernelDrivers.Snapshot(growing));
        Assert.Equal(4, growing.EnumerateCalls);

        var truncated = new FakeApi { TruncatePath = true };
        Assert.Null(KernelDrivers.Snapshot(truncated));
        Assert.Equal(2, truncated.EnumerateCalls);
    }

    private sealed class FakeApi : IKernelDriverApi
    {
        public int[] EnumerationCounts { get; init; } = [1];

        public int EnumerateCalls { get; private set; }

        public int NameFailuresRemaining { get; set; }

        public bool TruncatePath { get; init; }

        public bool Enumerate(Span<nint> bases, out uint needed)
        {
            int call = EnumerateCalls++;
            int count = EnumerationCounts[Math.Min(call, EnumerationCounts.Length - 1)];
            needed = checked((uint)(count * IntPtr.Size));
            for (int i = 0; i < Math.Min(count, bases.Length); i++)
            {
                bases[i] = (nint)(i + 1);
            }

            return true;
        }

        public uint GetBaseName(nint imageBase, Span<char> name)
        {
            if (NameFailuresRemaining-- > 0)
            {
                return 0;
            }

            return Copy($"driver{imageBase}.sys", name);
        }

        public uint GetFileName(nint imageBase, Span<char> path)
        {
            if (TruncatePath)
            {
                path[^1] = '\0';
                return (uint)(path.Length - 1);
            }

            return Copy($@"C:\Drivers\driver{imageBase}.sys", path);
        }

        private static uint Copy(string value, Span<char> buffer)
        {
            value.AsSpan().CopyTo(buffer);
            buffer[value.Length] = '\0';
            return (uint)value.Length;
        }
    }
}
