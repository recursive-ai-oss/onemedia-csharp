using RecursiveAI.OneMedia.Avi;
using RecursiveAI.OneMedia.Fuzz;

namespace RecursiveAI.OneMedia.Tests.Avi;

/// <summary>Inputs that once broke the AVI and DV readers, replayed through the fuzz targets, plus the hand-made cases behind them.</summary>
public class FuzzRegressionTests
{
    private static string Dir(string target) => System.IO.Path.Combine(AppContext.BaseDirectory, "testdata", "fuzz", target);

    [Fact]
    public void SavedAviInputsReplayWithoutAFinding()
    {
        string[] files = Directory.Exists(Dir("riff-avi")) ? Directory.GetFiles(Dir("riff-avi")) : [];
        Assert.NotEmpty(files);
        foreach (string f in files)
        {
            RiffAviTarget.Run(File.ReadAllBytes(f));
        }
    }

    [Fact]
    public void SavedDvInputsReplayWithoutAFinding()
    {
        foreach (string f in Directory.Exists(Dir("dv")) ? Directory.GetFiles(Dir("dv")) : [])
        {
            DvTarget.Run(File.ReadAllBytes(f));
        }
    }

    [Fact]
    public void AListTooShortForItsTypeInAForwardOnlyStreamDoesNotMoveTheReaderBack()
    {
        AviBuilder b = new();
        b.Streams.Add(new AviBuilder.StreamSpec("vids", "MJPG", 1, 25, 0, AviBuilder.Bitmap(8, 8, "MJPG")));
        for (int i = 0; i < 4; i++)
        {
            b.Chunks.Add(new AviBuilder.ChunkSpec(0, new byte[40]));
        }

        byte[] file = b.Build();
        int movi = file.AsSpan().IndexOf("movi"u8) + 4;
        // LIST with a size of 2: no room for the four-byte list type the reader has already consumed.
        byte[] bad = [.. file[..movi], .. "LIST"u8, 2, 0, 0, 0, 0x61, 0x62, .. file[movi..]];
        using AviDemuxer d = AviDemuxer.Open(new StreamForwardSource(new MemoryStream(bad)));
        while (d.TryReadPacket(out _))
        {
        }
    }
}
