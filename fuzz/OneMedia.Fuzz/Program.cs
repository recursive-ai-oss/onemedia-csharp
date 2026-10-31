using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using RecursiveAI.OneMedia.Fuzz;

// Fuzz targets for the parsers. Under libFuzzer (after SharpFuzz instrumentation of the library) run with the target name as the
// only argument; with a target name and file arguments each file is replayed once.
//   fuzz <target>               libFuzzer loop
//   fuzz <target> <file>...     replay
// Each parser family registers its targets in one file; add yours by adding a Register call below.
Dictionary<string, FuzzTarget> targets = new(StringComparer.Ordinal);
CodecFuzz.Register(targets);

if (args.Length == 0 || !targets.TryGetValue(args[0], out FuzzTarget? target))
{
    Console.Error.WriteLine("usage: fuzz <target> [file...]; targets: " + string.Join(", ", targets.Keys.OrderBy(k => k, StringComparer.Ordinal)));
    return 2;
}

if (args.Length == 1)
{
    SharpFuzz.Fuzzer.LibFuzzer.Run(span => target(span));
    return 0;
}

for (int i = 1; i < args.Length; i++)
{
    target(File.ReadAllBytes(args[i]));
}

Console.WriteLine($"replayed {args.Length - 1} input(s) without a crash");
return 0;
