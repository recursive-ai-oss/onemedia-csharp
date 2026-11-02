using System;
using System.Collections.Generic;
using System.Text;
using RecursiveAI.OneMedia;
using RecursiveAI.OneMedia.Codecs.Captions;
using RecursiveAI.OneMedia.Captions.Cea608;
using RecursiveAI.OneMedia.Captions.Cea708;
using RecursiveAI.OneMedia.Captions.Scc;

namespace RecursiveAI.OneMedia.Fuzz;

/// <summary>
/// Fuzz targets for embedded captions. <c>cc-data</c> takes a selector byte (the carriage and NAL length size) and one video sample: nothing may be thrown.
/// <c>cea608</c> takes byte pairs (field from the low bit of a third byte per group); <c>cea708</c> takes cc_data triplets (type, two data bytes);
/// <c>scc</c> takes text. The decoders must end every run with finite output.
/// </summary>
internal static class CaptionTargets
{
    public static void Register(Dictionary<string, FuzzTarget> targets)
    {
        targets["cc-data"] = CcData;
        targets["cea608"] = Cea608;
        targets["cea708"] = Cea708;
        targets["scc"] = Scc;
    }

    private static void CcData(ReadOnlySpan<byte> input)
    {
        if (input.Length < 2)
        {
            return;
        }

        var carriage = (CaptionCarriage)(input[0] % 3);
        var ex = new CaptionExtractor(carriage, input[0] / 3 % 5);
        List<CcTriplet> into = [];
        ex.Extract(input[1..], into);
        _ = CaptionData.TryGetFromItuT35(input[1..], out _);
        _ = CaptionData.TryGetFromMpeg2UserData(input[1..], out _);
        Span<CcTriplet> tmp = stackalloc CcTriplet[31];
        _ = CaptionData.ParseCcData(input[1..], tmp);
    }

    private static void Cea608(ReadOnlySpan<byte> input)
    {
        var dec = new Cea608Decoder { RequireParity = input.Length == 0 || (input[0] & 1) == 0 };
        for (int i = 1; i + 2 < input.Length; i += 3)
        {
            dec.Push(new MediaTime(i, 1000), input[i + 2] & 1, input[i], input[i + 1]);
        }

        dec.Finish(new MediaTime(input.Length, 1000));
        // The encoder must accept whatever text the decoder produced.
        foreach (Cea608Caption c in dec.Captions)
        {
            _ = Cea608Encoder.PopOn(c.Rows, c.Channel);
        }
    }

    private static void Cea708(ReadOnlySpan<byte> input)
    {
        var dec = new Cea708Decoder();
        for (int i = 0; i + 2 < input.Length; i += 3)
        {
            dec.Push(new MediaTime(i, 1000), 2 + (input[i] & 1), input[i + 1], input[i + 2]);
        }

        dec.Finish(new MediaTime(input.Length, 1000));
    }

    private static void Scc(ReadOnlySpan<byte> input)
    {
        string text = Encoding.Latin1.GetString(input);
        _ = SccSubtitleFormat.Instance.TryRead(input, out _);
        if (SccDocument.TryParse(text, out SccDocument? doc) && doc is not null)
        {
            _ = doc.Decode();
            string written = doc.Write();
            // The registry path: the format reads what the document's own writer produced.
            _ = SccSubtitleFormat.Instance.TryRead(Encoding.UTF8.GetBytes(written), out _);
            if (!SccDocument.TryParse(written, out SccDocument? again) || again!.Write() != written)
            {
                throw new InvalidOperationException("the canonical SCC form is not stable");
            }
        }
    }
}
