#if NET
using Xunit;

namespace DbSus.Tests.TestSupport;

// GC.GetAllocatedBytesForCurrentThread counts what the thread allocates, but when a background GC
// suspends the threads during a measurement, the unused rest of the thread's allocation context
// (at most a few KiB) is counted as allocated too. A measurement is therefore taken up to three
// times, each with its own warm-up, and passes when one of them allocated nothing: an allocation
// per record shows in every attempt, a suspension only in the attempt it falls on.
internal static class AllocationMeasurement
{
    public const int Attempts = 3;

    public static void AssertNoAllocation(Func<long> measure)
    {
        var attempts = new List<long>(Attempts);
        while (attempts.Count < Attempts)
        {
            var allocated = measure();
            if (allocated == 0)
            {
                return;
            }

            attempts.Add(allocated);
        }

        Assert.Fail($"Allocated {string.Join(", ", attempts)} bytes in {Attempts} attempts; the least must be 0.");
    }
}
#endif
