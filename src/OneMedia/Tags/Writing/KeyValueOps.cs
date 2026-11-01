using System.Linq;
using System.Globalization;

namespace RecursiveAI.OneMedia.Tags;

/// <summary>One change to a key/value tag system: set the key to the values, or (null) remove it.</summary>
/// <param name="Key">The native key in the spelling to write; matched against the file's keys per system rules.</param>
/// <param name="Values">The values, or null to remove every item with the key.</param>
/// <param name="Aliases">Other keys to remove when the key is removed (the same field under other names).</param>
internal sealed record KeyOp(string Key, IReadOnlyList<string>? Values, IReadOnlyList<string> Aliases);

/// <summary>How a system stores the number and total of a track or disc.</summary>
internal enum PairStyle
{
    None,

    /// <summary>One key holds <c>number/total</c> (ID3 TRCK, APE Track, RIFF ITRK).</summary>
    Combined,

    /// <summary>Two keys (Vorbis TRACKNUMBER and TRACKTOTAL).</summary>
    Separate,
}

/// <summary>
/// Turns common-field edits into key operations for a system, from the published mapping table: a field is written under the first key of its
/// row, and removed under every key of the row that is not the field's pair partner's. The same table feeds the common view, so what is written
/// is what the view reads back.
/// </summary>
internal static class KeyValueOps
{
    public static string? PrimaryKey(CommonField field, string system)
    {
        foreach (TagMappingRow r in TagMappingTable.Rows)
        {
            if (r.Field == field && r.System == system)
            {
                return r.Keys[0];
            }
        }

        return null;
    }

    public static IReadOnlyList<string> RowKeys(CommonField field, string system)
    {
        List<string> keys = [];
        foreach (TagMappingRow r in TagMappingTable.Rows)
        {
            if (r.Field == field && r.System == system)
            {
                keys.AddRange(r.Keys);
            }
        }

        return keys;
    }

    private static CommonField? Partner(CommonField f) => f switch
    {
        CommonField.TrackNumber => CommonField.TrackTotal,
        CommonField.TrackTotal => CommonField.TrackNumber,
        CommonField.DiscNumber => CommonField.DiscTotal,
        CommonField.DiscTotal => CommonField.DiscNumber,
        _ => null,
    };

    private static bool IsPair(CommonField f) => Partner(f) is not null;

    /// <summary>
    /// Builds the operations.
    /// </summary>
    /// <param name="system">The system.</param>
    /// <param name="edits">The edits.</param>
    /// <param name="existing">The first text value of a native key in the file (case-insensitive where the system is), or null.</param>
    /// <param name="pairStyle">How pairs are stored.</param>
    /// <param name="keyOverride">Per-field primary key replacing the table's (for example the TRACK key of a file that already uses it), or null.</param>
    /// <param name="ops">The key operations in order.</param>
    /// <returns>The report entry, without <c>Created</c>.</returns>
    public static SystemWriteReport Build(string system, TagEdits edits, Func<string, string?> existing, Func<CommonField, PairStyle> pairStyle, Func<CommonField, string?>? keyOverride, List<KeyOp> ops)
    {
        List<CommonField> written = [];
        List<CommonField> removed = [];
        List<(CommonField, string)> not = [];
        List<string> conv = [];
        HashSet<CommonField> pairDone = [];

        foreach (CommonField f in edits.TouchedFields)
        {
            IReadOnlyList<string>? change = edits.FieldChange(f);
            if (change is null)
            {
                continue;
            }

            if (IsPair(f))
            {
                CommonField num = f is CommonField.TrackNumber or CommonField.TrackTotal ? CommonField.TrackNumber : CommonField.DiscNumber;
                CommonField tot = num == CommonField.TrackNumber ? CommonField.TrackTotal : CommonField.DiscTotal;
                if (!pairDone.Add(num))
                {
                    continue;
                }

                PairStyle style = pairStyle(num);
                if (style == PairStyle.None)
                {
                    not.Add((f, $"{system} has no track or disc number"));
                    continue;
                }

                AddPair(system, edits, existing, style, num, tot, keyOverride, ops, written, removed);
                continue;
            }

            string? key = keyOverride?.Invoke(f) ?? PrimaryKey(f, system);
            if (key is null)
            {
                not.Add((f, $"{system} has no field for {f}"));
                continue;
            }

            if (change.Count == 0)
            {
                ops.Add(new KeyOp(key, null, RowKeys(f, system)));
                removed.Add(f);
            }
            else
            {
                if (change.Count > 1 && f != CommonField.Keywords)
                {
                    conv.Add($"{f}: {change.Count} values written as {change.Count} {key} items");
                }

                ops.Add(new KeyOp(key, change, []));
                written.Add(f);
            }
        }

        foreach (TagEdit n in edits.NativeFor(system))
        {
            ops.Add(n.Kind == TagEditKind.SetNative ? new KeyOp(n.Key!, n.Values, []) : new KeyOp(n.Key!, null, []));
        }

        return new SystemWriteReport(system, written, removed, not, conv, false);
    }

    private static void AddPair(string system, TagEdits edits, Func<string, string?> existing, PairStyle style, CommonField num, CommonField tot, Func<CommonField, string?>? keyOverride, List<KeyOp> ops, List<CommonField> written, List<CommonField> removed)
    {
        string numKey = keyOverride?.Invoke(num) ?? PrimaryKey(num, system)!;
        string? totKey = style == PairStyle.Separate ? (keyOverride?.Invoke(tot) ?? PrimaryKey(tot, system)) : null;
        (int? n, int? t) = Parse(existing(numKey));
        if (style == PairStyle.Separate && totKey is not null && Parse(existing(totKey)).Number is { } et)
        {
            t = et;
        }

        IReadOnlyList<string>? cn = edits.FieldChange(num);
        IReadOnlyList<string>? ct = edits.FieldChange(tot);
        if (cn is not null)
        {
            n = cn.Count > 0 && int.TryParse(cn[0], NumberStyles.None, CultureInfo.InvariantCulture, out int vn) ? vn : null;
            (n > 0 ? written : removed).Add(num);
        }

        if (ct is not null)
        {
            t = ct.Count > 0 && int.TryParse(ct[0], NumberStyles.None, CultureInfo.InvariantCulture, out int vt) ? vt : null;
            (t > 0 ? written : removed).Add(tot);
        }

        if (style == PairStyle.Combined)
        {
            if (n is null or 0)
            {
                ops.Add(new KeyOp(numKey, null, RowKeys(num, system)));
            }
            else
            {
                ops.Add(new KeyOp(numKey, [t is > 0 ? $"{n}/{t}" : n.Value.ToString(CultureInfo.InvariantCulture)], []));
            }

            return;
        }

        ops.Add(n is > 0 ? new KeyOp(numKey, [n.Value.ToString(CultureInfo.InvariantCulture)], []) : new KeyOp(numKey, null, RowKeys(num, system).Where(k => !string.Equals(k, totKey, StringComparison.OrdinalIgnoreCase)).ToArray()));
        if (totKey is not null)
        {
            ops.Add(t is > 0 ? new KeyOp(totKey, [t.Value.ToString(CultureInfo.InvariantCulture)], []) : new KeyOp(totKey, null, RowKeys(tot, system).Where(k => !string.Equals(k, numKey, StringComparison.OrdinalIgnoreCase) && !RowKeys(num, system).Contains(k, StringComparer.OrdinalIgnoreCase)).ToArray()));
        }
    }

    private static (int? Number, int? Total) Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return (null, null);
        }

        int slash = text.IndexOf('/', StringComparison.Ordinal);
        string a = slash < 0 ? text : text[..slash];
        string b = slash < 0 ? "" : text[(slash + 1)..];
        return (int.TryParse(a.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out int x) ? x : null, int.TryParse(b.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out int y) ? y : null);
    }
}
