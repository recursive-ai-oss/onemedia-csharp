using RecursiveAI.OneMedia.Matroska;
using static RecursiveAI.OneMedia.Tests.Ebl;

namespace RecursiveAI.OneMedia.Tests;

public class EbmlTreeTests
{
    private static EbmlDocument Doc(byte[] file, DefectLog? defects = null, MediaLimits? limits = null) =>
        EbmlDocument.Open(new MemoryMediaSource(file), limits, defects);

    private static byte[] Sample() => Cat(Header(), SegmentElement(
        InfoElement(),
        TracksElement(Track(1, 1, "V_VP8")),
        ClusterElement(0, Simple(1, 0, 0x80, Fill(10, 1)), Simple(1, 1, 0, Fill(5, 2))),
        El(0x1254C367, El(0x7373, El(0x63C0, U(0x68CA, 50))))));

    [Fact]
    public void TheTreeHoldsRangesAndFindsChildrenLazily()
    {
        byte[] file = Sample();
        EbmlDocument doc = Doc(file);
        Assert.Equal(2, doc.Elements.Count);
        Assert.Equal("EBML", doc.Header!.Name);
        EbmlElement segment = doc.Segment!;
        Assert.Equal(file.Length, segment.Offset + segment.HeaderSize + segment.PayloadSize);
        Assert.Equal(["Info", "Tracks", "Cluster", "Tags"], segment.ChildElements.Select(e => e.Name).ToArray());
        EbmlElement cluster = segment.ChildElements[2];
        Assert.Equal(["Timestamp", "SimpleBlock", "SimpleBlock"], cluster.ChildElements.Select(e => e.Name).ToArray());
        // The node does not hold content: its payload is read on request.
        EbmlElement block = cluster.ChildElements[1];
        byte[] payload = new byte[block.PayloadSize];
        Assert.Equal(payload.Length, block.ReadPayload(payload));
        Assert.Equal(Fill(10, 1), payload[4..]);
        Assert.Equal(file.Length, doc.Elements[1].Range.End);
        Assert.Empty(doc.Defects);
        IStructureNode node = segment;
        Assert.Equal(MatroskaIdsForTest.Cluster, node.FindChild(MatroskaIdsForTest.Cluster)!.TypeKey);
        Assert.Equal(5, node.Descendants().Count(n => ((EbmlElement)n.Node).Name == "SimpleBlock" || ((EbmlElement)n.Node).Name == "Timestamp" || ((EbmlElement)n.Node).Name == "Info" || ((EbmlElement)n.Node).Name == "Tracks"));
    }

    private static class MatroskaIdsForTest
    {
        public const ulong Cluster = 0x1F43B675;
    }

    [Fact]
    public void JunkBetweenSegmentChildrenIsSkippedWithADefectAndTheTreeContinues()
    {
        byte[] file = Cat(Header(), SegmentElement(
            InfoElement(),
            new byte[] { 0, 0, 0, 0, 0, 0, 0 },
            TracksElement(Track(1, 1, "V_VP8")),
            ClusterElement(0, Simple(1, 0, 0x80, Fill(10, 1)))));
        EbmlDocument doc = Doc(file);
        Assert.Equal(["Info", "Tracks", "Cluster"], doc.Segment!.ChildElements.Select(e => e.Name).ToArray());
        MediaDefect defect = Assert.Single(doc.Defects);
        Assert.Equal(DefectKind.UnaccountedBytes, defect.Kind);
        Assert.Equal(doc.Segment.ChildElements[0].Range.End, defect.Offset);
    }

    [Fact]
    public void UnknownSizeElementsGetTheirEndFromTheNextSibling()
    {
        byte[] file = Cat(Header(), SegmentUnknown(
            InfoElement(),
            ElUnknown(Cluster, U(0xE7, 0), Simple(1, 0, 0x80, Fill(10, 1))),
            ElUnknown(Cluster, U(0xE7, 5), Simple(1, 0, 0x80, Fill(10, 2))),
            El(0x1254C367, El(0x7373))));
        EbmlDocument doc = Doc(file);
        EbmlElement segment = doc.Segment!;
        Assert.True(segment.IsSizeInferred);
        Assert.Equal(["Info", "Cluster", "Cluster", "Tags"], segment.ChildElements.Select(e => e.Name).ToArray());
        EbmlElement first = segment.ChildElements[1];
        Assert.True(first.IsSizeInferred);
        Assert.Equal(["Timestamp", "SimpleBlock"], first.ChildElements.Select(e => e.Name).ToArray());
        Assert.Equal(segment.ChildElements[2].Offset, first.Offset + first.HeaderSize + first.PayloadSize);
        Assert.Equal(file.Length, segment.Offset + segment.HeaderSize + segment.PayloadSize);
    }

    [Fact]
    public void SizesPastTheParentOrTheFileAreClampedAndReported()
    {
        // A Cluster whose size claims 1000 bytes inside a Segment that has fewer.
        byte[] file = Cat(Header(), SegmentElement(InfoElement(), Cat(Id(Cluster), Size(1000, 2), U(0xE7, 0), Simple(1, 0, 0x80, Fill(5, 1)))));
        DefectLog defects = new();
        EbmlDocument doc = Doc(file, defects);
        EbmlElement cluster = doc.Segment!.ChildElements[1];
        Assert.True(cluster.IsSizeInferred);
        Assert.Equal(file.Length, cluster.Offset + cluster.HeaderSize + cluster.PayloadSize);
        Assert.Contains(defects, d => (d.Kind == DefectKind.SizeExceedsParent || d.Kind == DefectKind.SizeExceedsFile) && d.Offset == cluster.Offset);
        Assert.Equal(["Timestamp", "SimpleBlock"], cluster.ChildElements.Select(e => e.Name).ToArray());

        // A top-level element past the end of the data.
        byte[] cut = file.AsSpan(0, file.Length - 3).ToArray();
        DefectLog defects2 = new();
        EbmlDocument doc2 = Doc(cut, defects2);
        Assert.Contains(defects2, d => d.Kind == DefectKind.SizeExceedsFile);
        Assert.Equal(cut.Length, doc2.Segment!.Offset + doc2.Segment.HeaderSize + doc2.Segment.PayloadSize);
    }

    [Fact]
    public void UnknownElementsStayAsRanges()
    {
        byte[] vendor = El(0x3F1234, Fill(7, 1)); // an ID this reader has never heard of
        byte[] file = Cat(Header(), SegmentElement(InfoElement(), vendor, TracksElement(Track(1, 1, "V_VP8"))));
        EbmlElement segment = Doc(file).Segment!;
        EbmlElement unknown = segment.ChildElements[1];
        Assert.Null(unknown.Name);
        Assert.Equal(0x3F1234u, unknown.Id);
        Assert.False(unknown.IsMaster);
        Assert.Empty(unknown.Children);
        Assert.Equal(vendor.Length, unknown.Range.Length);
        Assert.Equal(vendor, file.AsSpan((int)unknown.Offset, vendor.Length).ToArray());
    }

    [Fact]
    public void CrcIsVerifiedAndAMismatchIsADefect()
    {
        byte[] Build(bool corrupt) => Cat(Header(), SegmentElement(WithCrc(Info, corrupt, U(0x2AD7B1, 1_000_000)), WithCrc(Tracks, false, Track(1, 1, "V_VP8")), InfoElement()));
        DefectLog defects = new();
        EbmlElement good = Doc(Build(false), defects).Segment!.ChildElements[0];
        Assert.Equal(EbmlCrcStatus.Valid, good.VerifyCrc());
        Assert.Empty(defects);
        DefectLog badLog = new();
        EbmlDocument bad = Doc(Build(true), badLog);
        Assert.Equal(EbmlCrcStatus.Mismatch, bad.Segment!.ChildElements[0].VerifyCrc());
        Assert.Equal(EbmlCrcStatus.Valid, bad.Segment.ChildElements[1].VerifyCrc());
        Assert.Equal(EbmlCrcStatus.None, bad.Segment.ChildElements[2].VerifyCrc());
        Assert.Equal(DefectKind.ChecksumMismatch, Assert.Single(badLog).Kind);
    }

    [Fact]
    public void InvalidHeadersWithNothingToResumeAtStopTheParentWithADefect()
    {
        byte[] file = Cat(Header(), SegmentElement(InfoElement(), [0x00, 0x01, 0x02]));
        DefectLog defects = new();
        EbmlElement segment = Doc(file, defects).Segment!;
        Assert.Equal(["Info"], segment.ChildElements.Select(e => e.Name).ToArray());
        Assert.Contains(defects, d => d.Kind == DefectKind.InvalidSize);
    }

    [Fact]
    public void DepthAndElementLimitsApply()
    {
        byte[] deep = Sample();
        EbmlDocument doc = Doc(deep, limits: new MediaLimits { MaxStructureDepth = 2 });
        Assert.Throws<MediaLimitExceededException>(() => doc.Segment!.ChildElements[2].ChildElements.ToArray());
        Assert.Throws<MediaLimitExceededException>(() => Doc(deep, limits: new MediaLimits { MaxElementCount = 3 }).Segment!.ChildElements.ToArray());
        Assert.Throws<MediaFormatException>(() => Doc("not ebml"u8.ToArray()));
    }

    [Fact]
    public void RandomBytesNeverEscapeAsUntypedExceptions()
    {
        byte[] basis = Sample();
        byte[] copy = new byte[basis.Length];
        for (int i = 0; i < basis.Length; i++)
        {
            foreach (byte value in new byte[] { 0x00, 0xFF, 0x01, 0x80 })
            {
                basis.CopyTo(copy, 0);
                copy[i] = value;
                try
                {
                    EbmlDocument doc = Doc(copy);
                    foreach (EbmlElement top in doc.Elements)
                    {
                        _ = ((IStructureNode)top).Descendants().Count();
                    }
                }
                catch (MediaException)
                {
                }
            }
        }
    }
}
