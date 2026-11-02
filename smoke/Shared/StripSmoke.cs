using RecursiveAI.OneMedia.Privacy;

namespace RecursiveAI.OneMedia.Smoke;

/// <summary>Strips the synthetic fixture under NativeAOT and in the browser WebAssembly runtime and checks that the secrets are gone and the length is the same.</summary>
internal static class StripSmoke
{
    public static void Run(Action<string, bool> check)
    {
        using Stream s = typeof(StripSmoke).Assembly.GetManifestResourceStream("strip-mixed.mp4")!;
        using MemoryStream m = new();
        s.CopyTo(m);
        byte[] original = m.ToArray();
        using MemoryMediaSource source = new(original);
        StripPlan plan = PrivacyStrip.Plan(source);
        using MemoryStream output = new();
        SafeWriter.Write(plan.Plan, source, output);
        byte[] stripped = output.ToArray();
        check("strip keeps length", stripped.Length == original.Length && plan.Plan.IsSameLayout);
        check("strip complete", plan.Report.IsComplete && plan.Report.Entries.Count > 10);
        check("strip removes serial", stripped.AsSpan().IndexOf("C0000000000042"u8) < 0 && original.AsSpan().IndexOf("C0000000000042"u8) >= 0);
        check("strip removes position", stripped.AsSpan().IndexOf("+47.6205-122.3493"u8) < 0 && stripped.AsSpan().IndexOf("47,37.230N"u8) < 0);
    }
}
