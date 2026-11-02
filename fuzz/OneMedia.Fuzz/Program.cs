using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using RecursiveAI.OneMedia.Fuzz;

// Fuzz targets for the parsers. Under libFuzzer (after SharpFuzz instrumentation of the library) run with no inputs; with file
// arguments each file is replayed once. The target is named by the ONEMEDIA_FUZZ_TARGET environment variable or, when that is
// unset, by the first argument:
//   ONEMEDIA_FUZZ_TARGET=<target> fuzz [file...]
//   fuzz <target> [file...]
// Each parser family registers its targets here.
Dictionary<string, FuzzTarget> targets = new(StringComparer.OrdinalIgnoreCase)
{
    ["isobmff"] = IsoBmffTarget.Run,
    ["matroska"] = MatroskaTarget.Run,
    ["tags"] = TagsTarget.Run,
    ["asf"] = AsfTarget.Run,
    ["registry"] = RegistryTarget.Run,
    ["riff-avi"] = RiffAviTarget.Run,
    ["dv"] = DvTarget.Run,
};
CodecFuzz.Register(targets);
MpegTarget.Register(targets);
AudioStreamFuzz.Register(targets);
XiphFlvTargets.Register(targets);
PcmTargets.Register(targets);
WriterTarget.Register(targets);
RemuxTarget.Register(targets);
TsMuxTarget.Register(targets);
TagWriteTarget.Register(targets);
DateWriteTarget.Register(targets);
FactsTargets.Register(targets);
TelemetryTarget.Register(targets);
StripTarget.Register(targets);
SubtitleTargets.Register(targets);
TextTrackTarget.Register(targets);
TeletextTarget.Register(targets);
BitmapSubtitleTargets.Register(targets);
CaptionTargets.Register(targets);
EventTargets.Register(targets);
ReconstructTarget.Register(targets);
RepairTarget.Register(targets);
WavMuxTarget.Register(targets);

string? name = Environment.GetEnvironmentVariable("ONEMEDIA_FUZZ_TARGET");
int first = 0;
if (string.IsNullOrEmpty(name) && args.Length > 0)
{
    name = args[0];
    first = 1;
}

if (string.IsNullOrEmpty(name) || !targets.TryGetValue(name, out FuzzTarget? target))
{
    Console.Error.WriteLine("usage: fuzz <target> [file...]; targets: " + string.Join(", ", targets.Keys.OrderBy(k => k, StringComparer.Ordinal)));
    return 2;
}

if (args.Length == first)
{
    SharpFuzz.Fuzzer.LibFuzzer.Run(span => target(span));
    return 0;
}

for (int i = first; i < args.Length; i++)
{
    target(File.ReadAllBytes(args[i]));
}

Console.WriteLine($"replayed {args.Length - first} input(s) without a crash");
return 0;
