// One UA node, independent of how an AML document writes it down.
//
// AML has at least three ways to point at a node of a running server: the
// NodeId attribute type of OPC 10000-83 Annex A, the OPCUAItem interface of
// the MTP (VDI/VDE/NAMUR 2658) and the DataVariable of the AutomationML best
// practice recommendation BPR 007. All three reduce to a namespace URI, an
// identifier type and an identifier, which is this record. Namespace indexes
// are never stored: they are only valid for one server session.

using System.Globalization;

namespace OpcUaAml.Addressing;

public enum UaIdType { Numeric, String, Guid, Opaque }

public sealed record UaNodeAddress(string NamespaceUri, UaIdType IdType, string Identifier, string? ServerUri = null)
{
    /// <summary>The ExpandedNodeId text form with the namespace URI, e.g. <c>nsu=http://x/;i=5</c>.</summary>
    public override string ToString() =>
        (ServerUri != null ? $"svu={ServerUri};" : "") + $"nsu={NamespaceUri};{Prefix(IdType)}={Identifier}";

    /// <summary>The NodeId text form with a namespace index, as a server session needs it.</summary>
    public string ToNodeIdString(IReadOnlyList<string> namespaceTable)
    {
        var index = -1;
        for (var i = 0; i < namespaceTable.Count; i++)
            if (namespaceTable[i] == NamespaceUri) { index = i; break; }
        if (index < 0) throw new FormatException($"Namespace '{NamespaceUri}' is not in the server's namespace table.");
        return (index == 0 ? "" : $"ns={index};") + $"{Prefix(IdType)}={Identifier}";
    }

    /// <summary>
    /// Parses <c>nsu=&lt;uri&gt;;i=5</c>, or <c>ns=2;s=Name</c> and <c>i=5</c> with a
    /// namespace table to resolve the index.
    /// </summary>
    public static UaNodeAddress Parse(string text, IReadOnlyList<string>? namespaceTable = null)
    {
        if (string.IsNullOrWhiteSpace(text)) throw new FormatException("Empty NodeId.");
        string? server = null;
        string? uri = null;
        var rest = text.Trim();

        if (rest.StartsWith("svu=", StringComparison.Ordinal))
        {
            var end = rest.IndexOf(';');
            if (end < 0) throw new FormatException($"'{text}': svu= without a NodeId.");
            server = rest[4..end];
            rest = rest[(end + 1)..];
        }
        if (rest.StartsWith("nsu=", StringComparison.Ordinal))
        {
            var end = LastSeparator(rest);
            if (end < 0) throw new FormatException($"'{text}': nsu= without an identifier.");
            uri = rest[4..end];
            rest = rest[(end + 1)..];
        }
        else if (rest.StartsWith("ns=", StringComparison.Ordinal))
        {
            var end = rest.IndexOf(';');
            if (end < 0 || !int.TryParse(rest[3..end], NumberStyles.None, CultureInfo.InvariantCulture, out var index))
                throw new FormatException($"'{text}': malformed namespace index.");
            if (namespaceTable == null || index >= namespaceTable.Count)
                throw new FormatException($"'{text}': namespace index {index} cannot be resolved without the server's namespace table.");
            uri = namespaceTable[index];
            rest = rest[(end + 1)..];
        }
        else
        {
            uri = namespaceTable is { Count: > 0 } ? namespaceTable[0] : "http://opcfoundation.org/UA/";
        }

        if (rest.Length < 2 || rest[1] != '=') throw new FormatException($"'{text}': missing identifier type (i=, s=, g=, b=).");
        var type = rest[0] switch
        {
            'i' => UaIdType.Numeric,
            's' => UaIdType.String,
            'g' => UaIdType.Guid,
            'b' => UaIdType.Opaque,
            _ => throw new FormatException($"'{text}': unknown identifier type '{rest[0]}'."),
        };
        var id = rest[2..];
        if (type == UaIdType.Numeric && !uint.TryParse(id, NumberStyles.None, CultureInfo.InvariantCulture, out _))
            throw new FormatException($"'{text}': numeric identifier expected.");
        if (type == UaIdType.Guid && !Guid.TryParse(id, out _))
            throw new FormatException($"'{text}': GUID identifier expected.");
        return new UaNodeAddress(uri, type, id, server);
    }

    public static bool TryParse(string text, IReadOnlyList<string>? namespaceTable, out UaNodeAddress? address)
    {
        try { address = Parse(text, namespaceTable); return true; }
        catch (FormatException) { address = null; return false; }
    }

    private static string Prefix(UaIdType type) => type switch
    {
        UaIdType.Numeric => "i",
        UaIdType.String => "s",
        UaIdType.Guid => "g",
        _ => "b",
    };

    /// <summary>
    /// The separator between namespace URI and identifier. URIs may contain ';'
    /// themselves, so the last one before an identifier prefix wins.
    /// </summary>
    private static int LastSeparator(string text)
    {
        for (var i = text.Length - 3; i > 4; i--)
            if (text[i] == ';' && text[i + 2] == '=' && "isgb".Contains(text[i + 1])) return i;
        return -1;
    }
}
