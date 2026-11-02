using System.Globalization;
using System.Linq;
using RecursiveAI.OneMedia.IsoBmff;

namespace RecursiveAI.OneMedia.Streaming.Sources;

/// <summary>
/// Turns a DASH manifest, read by the shared manifest model (<see cref="DashMpd"/>), into the plan a presentation plays: the variants, and for the chosen
/// one a rendition per adaptation set (across periods) with its segments expanded from <c>SegmentTemplate</c>, <c>SegmentList</c>, <c>SegmentBase</c> with an
/// index range (the subsegments then come from the <c>sidx</c> box) or a single resource.
/// </summary>
internal static class DashReader
{
    /// <summary>True when the head holds an <c>MPD</c> root element start tag.</summary>
    public static bool IsManifest(ReadOnlySpan<byte> head)
    {
        // The root may follow an XML declaration and comments, so look through the head.
        int at = head.IndexOf("<MPD"u8);
        if (at < 0 || head.Length <= at + 4)
        {
            return false;
        }

        return head[at + 4] is (byte)' ' or (byte)'>' or (byte)'\r' or (byte)'\n' or (byte)'\t';
    }

    /// <summary>Reads the manifest. All variants are listed; renditions are built for <paramref name="variantIndex"/> only (clamped).</summary>
    /// <exception cref="ManifestFormatException">The manifest is not well-formed or not an MPD.</exception>
    public static PresentationPlan Read(ReadOnlySpan<byte> bytes, Uri location, int variantIndex, MediaLimits limits, DefectLog defects)
    {
        DashMpd mpd = DashMpd.Parse(bytes, limits);
        PresentationPlan plan = new();
        if (mpd.Type == DashPresentationType.Dynamic)
        {
            defects.Add(DefectKind.Unsupported, DefectSeverity.Info, 0, "A dynamic manifest is read as it stands; it is not reloaded.", "dash.dynamic");
        }

        MediaTime mpdDuration = mpd.MediaPresentationDuration ?? MediaTime.Unknown;
        plan.Duration = mpdDuration;
        Uri mpdBase = ApplyBase(location, mpd.BaseUrls);
        IReadOnlyList<DashPeriod> periods = mpd.Periods;

        // The variants are the representations of the first video adaptation set (the first one when there is no video) of the first period.
        if (periods.Count > 0)
        {
            DashAdaptationSet? primary = null;
            foreach (DashAdaptationSet a in periods[0].AdaptationSets)
            {
                primary ??= a;
                if (KindOf(a, null) == TrackKind.Video)
                {
                    primary = a;
                    break;
                }
            }

            foreach (DashRepresentation rep in primary?.Representations ?? [])
            {
                limits.CheckSegments(plan.Variants.Count + 1, -1);
                plan.Variants.Add(new PresentationVariant(plan.Variants.Count, rep.Bandwidth, rep.Codecs ?? primary!.Codecs, rep.Width > 0 ? rep.Width : primary!.Width, rep.Height > 0 ? rep.Height : primary!.Height, rep.Id));
            }
        }

        if (plan.Variants.Count == 0)
        {
            plan.Variants.Add(new PresentationVariant(0, 0, null, 0, 0, null));
        }

        int chosen = Math.Clamp(variantIndex, 0, plan.Variants.Count - 1);
        for (int v = 0; v < plan.Variants.Count; v++)
        {
            plan.VariantRenditions.Add([]);
        }

        List<PlannedRendition> renditions = plan.VariantRenditions[chosen];
        Dictionary<(TrackKind, int), PlannedRendition> slots = [];
        MediaTime nextPeriodStart = MediaTime.Zero;
        long segmentCount = 0;
        for (int p = 0; p < periods.Count; p++)
        {
            DashPeriod period = periods[p];
            MediaTime start = period.Start ?? nextPeriodStart;
            MediaTime duration = period.Duration
                ?? (p + 1 < periods.Count && periods[p + 1].Start is { } next ? next.Subtract(start, MediaRounding.NearestAwayFromZero)
                : mpdDuration.IsKnown ? mpdDuration.Subtract(start, MediaRounding.NearestAwayFromZero) : MediaTime.Unknown);
            nextPeriodStart = duration.IsKnown ? start.Add(duration, MediaRounding.NearestAwayFromZero) : start;
            Uri periodBase = ApplyBase(mpdBase, period.BaseUrls);
            Dictionary<TrackKind, int> ordinal = [];
            bool firstOfPeriod = true;
            foreach (DashAdaptationSet set in period.AdaptationSets)
            {
                if (set.Representations.Count == 0)
                {
                    continue;
                }

                DashRepresentation rep = set.Representations[Math.Min(chosen, set.Representations.Count - 1)];
                TrackKind kind = KindOf(set, rep);
                int ord = ordinal.GetValueOrDefault(kind);
                ordinal[kind] = ord + 1;
                if (!slots.TryGetValue((kind, ord), out PlannedRendition? r))
                {
                    r = new PlannedRendition { KindHint = kind, Id = rep.Id, Codecs = rep.Codecs ?? set.Codecs, Language = set.Language, Bandwidth = rep.Bandwidth };
                    slots[(kind, ord)] = r;
                    renditions.Add(r);
                }

                bool encrypted = set.Descriptors.Any(d => d.Element == "ContentProtection") || rep.Descriptors.Any(d => d.Element == "ContentProtection");
                r.Protected |= encrypted;
                try
                {
                    AppendRepresentation(r, set, rep, ApplyBase(ApplyBase(periodBase, set.BaseUrls), rep.BaseUrls), start, duration, p > 0 && firstOfPeriod, encrypted, ref segmentCount, limits, defects);
                }
                catch (ManifestFormatException e)
                {
                    defects.Add(DefectKind.InvalidValue, DefectSeverity.Warning, 0, "A representation's segments cannot be listed: " + e.Message, "dash.bad-segments");
                }

                firstOfPeriod = false;
            }
        }

        return plan;
    }

    private static void AppendRepresentation(PlannedRendition r, DashAdaptationSet set, DashRepresentation rep, Uri repBase, MediaTime periodStart, MediaTime periodDuration, bool newPeriod, bool encrypted, ref long segmentCount, MediaLimits limits, DefectLog defects)
    {
        bool discontinuity = newPeriod;
        DashSegmentTemplate? template = rep.SegmentTemplate ?? set.SegmentTemplate;
        if (template is not null)
        {
            long scale = Math.Max(template.Timescale, 1);
            MediaTime offset = periodStart.Subtract(new MediaTime(template.PresentationTimeOffset, scale), MediaRounding.NearestAwayFromZero);
            PlannedMap? map = null;
            if (template.Initialization is { } init && TryUri(repBase, DashSegmentTemplate.Expand(init, rep.Id, rep.Bandwidth, 0, 0), out Uri? iu))
            {
                map = new PlannedMap(iu!, 0, -1);
            }

            foreach (DashSegmentRef s in template.Segments(rep.Id, rep.Bandwidth, periodDuration, limits))
            {
                limits.CheckSegments(++segmentCount, -1);
                if (TryUri(repBase, s.Uri, out Uri? su))
                {
                    r.Segments.Add(new PlannedSegment { Location = su!, Duration = new MediaTime(s.Duration, scale), Discontinuity = discontinuity, Init = map, Encrypted = encrypted, TimelineOffset = offset, Sequence = s.Number });
                    discontinuity = false;
                }
            }

            return;
        }

        if (rep.SegmentList is { } list)
        {
            long scale = Math.Max(list.Timescale, 1);
            MediaTime offset = periodStart.Subtract(new MediaTime(list.PresentationTimeOffset, scale), MediaRounding.NearestAwayFromZero);
            PlannedMap? map = null;
            // An initialization without a source URL is a byte range of the representation's own resource.
            if (list.Initialization is { } ie && TryUri(repBase, ie.SourceUrl ?? string.Empty, out Uri? iu))
            {
                TryRange(ie.Range, out long io, out long il);
                map = new PlannedMap(iu!, io, il);
            }

            long seq = list.StartNumber;
            foreach (DashSegmentUrl u in list.Segments)
            {
                limits.CheckSegments(++segmentCount, -1);
                TryRange(u.MediaRange, out long so, out long sl);
                if (TryUri(repBase, u.Media ?? string.Empty, out Uri? su))
                {
                    r.Segments.Add(new PlannedSegment { Location = su!, Offset = so, Length = sl, Duration = list.Duration > 0 ? new MediaTime(list.Duration, scale) : MediaTime.Unknown, Discontinuity = discontinuity, Init = map, Encrypted = encrypted, TimelineOffset = offset, Sequence = seq });
                    discontinuity = false;
                }

                seq++;
            }

            return;
        }

        if (rep.SegmentBase is { IndexRange: { } indexRange } baseInfo && TryRange(indexRange, out long ixo, out long ixl) && ixl > 0)
        {
            long scale = Math.Max(baseInfo.Timescale, 1);
            r.IndexRange = (ixo, ixl);
            r.IndexedResource = repBase;
            r.IndexedEncrypted = encrypted;
            r.IndexedOffset = periodStart.Subtract(new MediaTime(baseInfo.PresentationTimeOffset, scale), MediaRounding.NearestAwayFromZero);
            if (baseInfo.Initialization is { } ie2 && TryRange(ie2.Range, out long io2, out long il2) && il2 > 0)
            {
                r.IndexedInit = new PlannedMap(repBase, io2, il2);
            }

            return;
        }

        // No segment information: the representation is one resource, a plain file.
        limits.CheckSegments(++segmentCount, -1);
        r.Segments.Add(new PlannedSegment { Location = repBase, Duration = periodDuration, Discontinuity = discontinuity, Encrypted = encrypted, TimelineOffset = periodStart });
    }

    /// <summary>Turns the <c>sidx</c> box of a single-file representation into its subsegments' byte ranges.</summary>
    public static void ExpandIndex(PlannedRendition r, SegmentIndexBox index, MediaLimits limits)
    {
        long position = index.BoxEnd + (long)Math.Min(index.FirstOffset, long.MaxValue / 4);
        long seq = 1;
        foreach (SegmentReference reference in index.References)
        {
            limits.CheckSegments(r.Segments.Count + 1, index.BoxOffset);
            if (reference.ReferencesIndex)
            {
                // A hierarchical index points at further index boxes, which this reader does not follow.
                position += reference.Size;
                continue;
            }

            r.Segments.Add(new PlannedSegment
            {
                Location = r.IndexedResource!,
                Offset = position,
                Length = reference.Size,
                Duration = index.Timescale > 0 ? new MediaTime(reference.Duration, index.Timescale) : MediaTime.Unknown,
                Init = r.IndexedInit,
                Encrypted = r.IndexedEncrypted,
                TimelineOffset = r.IndexedOffset,
                Sequence = seq++,
            });
            position += reference.Size;
        }
    }

    // "first-last" as DASH writes byte ranges; a missing or malformed range means the whole resource.
    private static bool TryRange(string? text, out long offset, out long length)
    {
        offset = 0;
        length = -1;
        if (text is null)
        {
            return false;
        }

        int dash = text.IndexOf('-', StringComparison.Ordinal);
        if (dash <= 0 || !long.TryParse(text.AsSpan(0, dash), NumberStyles.None, CultureInfo.InvariantCulture, out long first))
        {
            return false;
        }

        offset = first;
        if (dash + 1 < text.Length && long.TryParse(text.AsSpan(dash + 1), NumberStyles.None, CultureInfo.InvariantCulture, out long last) && last >= first)
        {
            length = last - first + 1;
        }

        return true;
    }

    private static bool TryUri(Uri baseUri, string reference, out Uri? result)
    {
        result = null;
        try
        {
            return Uri.TryCreate(baseUri, reference, out result) && result is not null;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static Uri ApplyBase(Uri current, IReadOnlyList<string> baseUrls) => baseUrls.Count > 0 && TryUri(current, baseUrls[0].Trim(), out Uri? u) ? u! : current;

    private static TrackKind KindOf(DashAdaptationSet set, DashRepresentation? rep)
    {
        string key = set.ContentType ?? set.MimeType ?? rep?.MimeType ?? string.Empty;
        return key.StartsWith("video", StringComparison.Ordinal) ? TrackKind.Video
            : key.StartsWith("audio", StringComparison.Ordinal) ? TrackKind.Audio
            : key.StartsWith("text", StringComparison.Ordinal) || key.StartsWith("application", StringComparison.Ordinal) ? TrackKind.Subtitle
            : TrackKind.Unknown;
    }
}
