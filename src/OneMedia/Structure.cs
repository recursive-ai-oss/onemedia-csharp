namespace RecursiveAI.OneMedia;

/// <summary>
/// The minimal shape shared by the nodes of every structure tree (ISO boxes, EBML elements, RIFF chunks, Ogg pages…): where the
/// node sits in the source, how its header and payload split, a key naming its type, and its children on demand. A node never
/// holds the file's content, only positions; reading content is a separate, explicit step on the format's own node type.
/// </summary>
/// <remarks>Each format defines its own node type and implements this interface, so tools that only need to walk and report
/// offsets (a dumper, a differential harness) work across formats.</remarks>
public interface IStructureNode
{
    /// <summary>
    /// The node's type as a number: a box's FourCC read big-endian (so <c>moov</c> is 0x6D6F6F76), an EBML element ID with its
    /// length marker bits kept (so Segment is 0x18538067), a RIFF chunk's FourCC. Formats whose types are longer (ISO <c>uuid</c>
    /// extended types) expose the detail on their own node type.
    /// </summary>
    ulong TypeKey { get; }

    /// <summary>Absolute offset of the node's first header byte.</summary>
    long Offset { get; }

    /// <summary>Length of the header (type, size and any extra fields) in bytes.</summary>
    int HeaderSize { get; }

    /// <summary>
    /// Length of the payload in bytes. When the size field says "until the end" (ISO size 0, an EBML unknown size) this is the
    /// length the reader worked out, and <see cref="IsSizeInferred"/> is true.
    /// </summary>
    long PayloadSize { get; }

    /// <summary>True when the stored size was a marker (to end of file or parent, unknown) and <see cref="PayloadSize"/> was inferred.</summary>
    bool IsSizeInferred { get; }

    /// <summary>
    /// The child nodes in file order. Parsed on first access and cached, so walking a tree reads only the headers on the way.
    /// Empty for nodes that hold data rather than children. Problems found while parsing go to the tree's <see cref="DefectLog"/>.
    /// </summary>
    /// <exception cref="MediaLimitExceededException">The tree is deeper or larger than the limits allow.</exception>
    IReadOnlyList<IStructureNode> Children { get; }

    /// <summary>The whole node, header included.</summary>
    ByteRange Range => new(Offset, HeaderSize + PayloadSize);

    /// <summary>The node's payload.</summary>
    ByteRange Payload => new(Offset + HeaderSize, PayloadSize);
}

/// <summary>Walks over any <see cref="IStructureNode"/> tree.</summary>
public static class StructureNodeExtensions
{
    /// <summary>
    /// Enumerates the node and everything below it in file order (parents before children). Uses an explicit stack, so depth never
    /// threatens the call stack, and enforces the limits on the way.
    /// </summary>
    /// <param name="root">Where to start; included in the result.</param>
    /// <param name="limits">Depth and element-count ceilings; <see cref="MediaLimits.Default"/> if null.</param>
    /// <returns>Each node with its depth (the root is 1).</returns>
    /// <exception cref="ArgumentNullException"><paramref name="root"/> is null.</exception>
    /// <exception cref="MediaLimitExceededException">The tree is deeper than <see cref="MediaLimits.MaxStructureDepth"/> or holds more than <see cref="MediaLimits.MaxElementCount"/> nodes.</exception>
    public static IEnumerable<(IStructureNode Node, int Depth)> Descendants(this IStructureNode root, MediaLimits? limits = null)
    {
        ArgumentNullException.ThrowIfNull(root);
        limits ??= MediaLimits.Default;
        Stack<(IStructureNode Node, int Depth)> pending = new();
        pending.Push((root, 1));
        long count = 0;
        while (pending.Count > 0)
        {
            (IStructureNode node, int depth) = pending.Pop();
            limits.CheckDepth(depth, node.Offset);
            limits.CheckElementCount(++count, node.Offset);
            yield return (node, depth);
            IReadOnlyList<IStructureNode> children = node.Children;
            for (int i = children.Count - 1; i >= 0; i--)
            {
                pending.Push((children[i], depth + 1));
            }
        }
    }

    /// <summary>The first direct child with the given type key, or null.</summary>
    /// <param name="node">The parent.</param>
    /// <param name="typeKey">The wanted <see cref="IStructureNode.TypeKey"/>.</param>
    /// <returns>The child, or null.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="node"/> is null.</exception>
    public static IStructureNode? FindChild(this IStructureNode node, ulong typeKey)
    {
        ArgumentNullException.ThrowIfNull(node);
        foreach (IStructureNode child in node.Children)
        {
            if (child.TypeKey == typeKey)
            {
                return child;
            }
        }

        return null;
    }
}
