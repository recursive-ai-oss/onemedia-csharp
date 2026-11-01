namespace RecursiveAI.OneMedia.Tests;

/// <summary>
/// Tests that count allocated bytes run one at a time, after the parallel part of the run: another test's thread contending for the same
/// pools, JIT queue and tiering work must not land on the measured thread. The counter is already per thread; the collection removes the
/// remaining noise without loosening any assertion.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class AllocationMeasurements
{
    /// <summary>The collection name.</summary>
    public const string Name = "allocation-measurements";
}

internal static class AllocationGuard
{
    /// <summary>Skips the test in a build with the JIT optimiser disabled (Debug), where code allocates in ways Release does not.</summary>
    public static void RequireOptimised() =>
        Assert.SkipWhen(
            System.Diagnostics.Debugger.IsAttached
            || Unoptimised(typeof(AllocationGuard).Assembly)
            || Unoptimised(typeof(MediaFile).Assembly),
            "needs an optimised build");

    /// <summary>
    /// The fewest bytes a measurement allocated over up to <paramref name="passes"/> passes, stopping at the first pass that allocated nothing.
    /// A cost per packet appears in every pass; a one-off that lands on the measuring thread (a pool buffer the runtime trimmed after a
    /// collection, a type load) appears in one. Asserting that the best pass is zero keeps the guarantee and ignores the one-offs.
    /// </summary>
    public static long Best(Func<long> pass, int passes = 5)
    {
        long best = long.MaxValue;
        for (int i = 0; i < passes && best != 0; i++)
        {
            best = Math.Min(best, pass());
        }

        return best;
    }

    private static bool Unoptimised(System.Reflection.Assembly assembly) =>
        assembly.GetCustomAttributes(typeof(System.Diagnostics.DebuggableAttribute), false).Cast<System.Diagnostics.DebuggableAttribute>().Any(a => a.IsJITOptimizerDisabled);
}
