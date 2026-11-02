using RecursiveAI.OneMedia.Captions;
using RecursiveAI.OneMedia.Captions.Cea608;
using RecursiveAI.OneMedia.Captions.Scc;
using RecursiveAI.OneMedia.Codecs.Captions;

namespace RecursiveAI.OneMedia.Smoke;

/// <summary>Writes a caption as SCC, reads it back through the registry format, and extracts it from a synthetic SEI under NativeAOT and in the browser WebAssembly runtime.</summary>
internal static class CaptionSmoke
{
    public static void Run(Action<string, bool> check)
    {
        var cap = new Cea608Caption(Cea608Channel.CC1, new MediaTime(5005, 1000), new MediaTime(7007, 1000), [new Cea608Row(15, 3, "Hello caf\u00e9", [])]);
        string scc = SccDocument.FromCaptions([cap]).Write();
        SubtitleDocument doc = SccSubtitleFormat.Instance.ReadText(scc);
        check("scc round trip", doc.Cues.Count == 1 && doc.Cues[0].Text == "Hello caf\u00e9" && doc.Cues[0].Start == cap.Start);

        // One picture whose SEI carries the same 608 pairs, in a 4-byte length-prefixed sample.
        List<CcTriplet> triplets = [];
        foreach ((byte a, byte b) in Cea608Encoder.PopOn(cap.Rows))
        {
            triplets.Add(new CcTriplet(true, 0, a, b));
        }

        var frames = new List<CaptionFrame>();
        for (int i = 0; i < triplets.Count; i++)
        {
            byte[] payload = CaptionData.BuildItuT35Payload([triplets[i]]);
            List<byte> nal = [0x06, 4, (byte)payload.Length, .. payload, 0x80];
            List<byte> sample = [0, 0, 0, (byte)nal.Count, .. nal, 0, 0, 0, 2, 0x65, 0x88];
            var found = new List<CcTriplet>();
            new CaptionExtractor(CaptionCarriage.H264Sei, 4).Extract(sample.ToArray(), found);
            frames.Add(new CaptionFrame(new MediaTime(i * 1001, 30000), new MediaTime(1001, 30000), found));
        }

        EmbeddedCaptions decoded = EmbeddedCaptions.Decode(frames);
        check("sei extraction decodes", decoded.Cea608.Count == 1 && decoded.Cea608[0].Text == "Hello caf\u00e9");
    }
}
