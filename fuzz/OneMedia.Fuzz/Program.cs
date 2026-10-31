using System;
using System.Collections.Generic;
using System.IO;
using RecursiveAI.OneMedia.Fuzz;
using SharpFuzz;

// Fuzz targets, one per parser. The target is chosen by the ONEMEDIA_FUZZ_TARGET environment variable (default: isobmff).
// Under libFuzzer (after SharpFuzz instrumentation of the library) run with no arguments; with file arguments the inputs are replayed once.
Dictionary<string, Action<ReadOnlySpan<byte>>> targets = new(StringComparer.OrdinalIgnoreCase)
{
    ["isobmff"] = IsoBmffTarget.Run,
};

string name = Environment.GetEnvironmentVariable("ONEMEDIA_FUZZ_TARGET") ?? "isobmff";
if (!targets.TryGetValue(name, out Action<ReadOnlySpan<byte>>? run))
{
    Console.Error.WriteLine($"No fuzz target '{name}'. Known: {string.Join(", ", targets.Keys)}");
    return 1;
}

if (args.Length == 0)
{
    Fuzzer.LibFuzzer.Run(span => run(span));
    return 0;
}

foreach (string path in args)
{
    run(File.ReadAllBytes(path));
}

Console.WriteLine($"replayed {args.Length} input(s) without a crash");
return 0;
