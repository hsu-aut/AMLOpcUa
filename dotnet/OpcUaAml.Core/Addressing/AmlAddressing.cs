// Reading and writing UA node addresses in the three AML conventions:
//
// - Annex A (OPC 10000-83): an attribute of type NodeId with RootNodeId
//   (ExplicitNodeId: NamespaceUri and one of NumericId/StringId/GuidId/
//   OpaqueId), optionally ServerInstanceUri; Alias and BrowsePath address a
//   node indirectly and are reported, not resolved, here.
// - MTP (VDI/VDE/NAMUR 2658): an ExternalInterface of class OPCUAItem with
//   Identifier (its AttributeDataType gives the identifier type), Namespace
//   (the URI) and Access.
// - BPR 007 DataVariable (AutomationML e.V., 2017): an attribute with
//   sub-attributes NodeId ("ns=1;i=123") and RefDataSource (the ID of an
//   element with role OPCUA-Server, whose NameSpaceTable resolves the index).

using Aml.Engine.CAEX;
using Aml.Engine.CAEX.Extensions;

namespace OpcUaAml.Addressing;

public sealed class AddressingException : Exception
{
    public AddressingException(string message) : base(message) { }
}

public static class AnnexANodeId
{
    public const string AttributeName = "NodeId";
    private const string NodeIdType = "[ATL_http://opcfoundation.org/UA/]/[NodeId]";
    private const string ExplicitNodeIdType = "ATL_OpcAmlMetaModel/ExplicitNodeId";
    private const string NamespaceUriType = "ATL_OpcAmlMetaModel/NamespaceUri";

    /// <summary>Reads a NodeId attribute. Throws for Alias or BrowsePath forms, which need a server to resolve.</summary>
    public static UaNodeAddress Read(AttributeType nodeId)
    {
        var root = Sub(nodeId, "RootNodeId");
        var server = Value(Sub(nodeId, "ServerInstanceUri"));
        if (root == null)
        {
            if (Sub(nodeId, "Alias") != null)
                throw new AddressingException($"'{nodeId.Name}' addresses its node by alias; resolving it needs an alias server.");
            throw new AddressingException($"'{nodeId.Name}' has no RootNodeId.");
        }
        var browsePath = Sub(nodeId, "BrowsePath");
        if (browsePath != null && browsePath.Attribute.Count > 0 && Sub(browsePath, "Elements")?.Attribute.Count > 0)
            throw new AddressingException($"'{nodeId.Name}' addresses its node by a BrowsePath from its RootNodeId; resolving it needs a server.");

        var uri = Value(Sub(root, "NamespaceUri"))
            ?? throw new AddressingException($"'{nodeId.Name}': RootNodeId has no NamespaceUri.");
        foreach (var (name, type) in new[] { ("NumericId", UaIdType.Numeric), ("StringId", UaIdType.String), ("GuidId", UaIdType.Guid), ("OpaqueId", UaIdType.Opaque) })
        {
            var v = Value(Sub(root, name));
            if (v != null) return new UaNodeAddress(uri, type, v, server);
        }
        throw new AddressingException($"'{nodeId.Name}': RootNodeId has no identifier.");
    }

    /// <summary>Finds and reads the NodeId attribute of an element, or returns null if it has none.</summary>
    public static UaNodeAddress? Of(IObjectWithAttributes owner) =>
        owner.Attribute[AttributeName] is { } a ? Read(a) : null;

    /// <summary>Writes the address as the element's NodeId attribute, replacing an existing one.</summary>
    public static AttributeType Write(IObjectWithAttributes owner, UaNodeAddress address)
    {
        if (owner.Attribute[AttributeName] is { } old) owner.Attribute.RemoveElement(old);
        var nodeId = owner.Attribute.Append(AttributeName);
        nodeId.RefAttributeType = NodeIdType;
        if (address.ServerUri != null)
            Set(nodeId.Attribute.Append("ServerInstanceUri"), "xs:anyURI", address.ServerUri);

        var root = nodeId.Attribute.Append("RootNodeId");
        root.RefAttributeType = ExplicitNodeIdType;
        var ns = root.Attribute.Append("NamespaceUri");
        ns.RefAttributeType = NamespaceUriType;
        Set(ns, "xs:anyURI", address.NamespaceUri);
        var (name, xsType) = address.IdType switch
        {
            UaIdType.Numeric => ("NumericId", "xs:long"),
            UaIdType.String => ("StringId", "xs:string"),
            UaIdType.Guid => ("GuidId", "xs:string"),
            _ => ("OpaqueId", "xs:base64Binary"),
        };
        Set(root.Attribute.Append(name), xsType, address.Identifier);
        return nodeId;
    }

    internal static AttributeType? Sub(IObjectWithAttributes? owner, string name) => owner?.Attribute[name];

    internal static string? Value(AttributeType? a) => string.IsNullOrEmpty(a?.Value) ? null : a!.Value;

    internal static void Set(AttributeType a, string type, string value)
    {
        a.AttributeDataType = type;
        a.Value = value;
    }
}

public sealed record MtpItem(UaNodeAddress Address, int? Access);

public static class MtpOpcUaItem
{
    public const string ClassPath = "MTPCommunicationICLib/DataItem/OPCUAItem";

    public static bool IsItem(ExternalInterfaceType ei) =>
        ei.RefBaseClassPath?.EndsWith("OPCUAItem", StringComparison.Ordinal) == true;

    public static MtpItem Read(ExternalInterfaceType item)
    {
        var identifier = item.Attribute["Identifier"]
            ?? throw new AddressingException($"'{item.Name}' has no Identifier attribute.");
        var ns = AnnexANodeId.Value(item.Attribute["Namespace"])
            ?? throw new AddressingException($"'{item.Name}' has no Namespace value.");
        var id = identifier.Value ?? throw new AddressingException($"'{item.Name}' has no identifier value.");

        // VDI 2658: "xs:string = string; xs:ID = GUID; xs:Base64Binary = ByteArray; xs:int = Integer".
        var type = identifier.AttributeDataType?.ToLowerInvariant() switch
        {
            "xs:id" => UaIdType.Guid,
            "xs:base64binary" => UaIdType.Opaque,
            "xs:int" or "xs:integer" or "xs:unsignedint" or "xs:long" => UaIdType.Numeric,
            _ => UaIdType.String,
        };
        int? access = int.TryParse(item.Attribute["Access"]?.Value, out var a) ? a : null;
        return new MtpItem(new UaNodeAddress(ns, type, id), access);
    }

    /// <summary>Writes an OPCUAItem interface as the MTP files do.</summary>
    public static ExternalInterfaceType Write(SystemUnitClassType owner, string name, UaNodeAddress address, int access = 3)
    {
        var ei = owner.ExternalInterface.Append(name);
        ei.RefBaseClassPath = ClassPath;
        AnnexANodeId.Set(ei.Attribute.Append("Access"), "xs:unsignedByte", access.ToString());
        var type = address.IdType switch
        {
            UaIdType.Guid => "xs:ID",
            UaIdType.Opaque => "xs:base64Binary",
            UaIdType.Numeric => "xs:int",
            _ => "xs:string",
        };
        AnnexANodeId.Set(ei.Attribute.Append("Identifier"), type, address.Identifier);
        AnnexANodeId.Set(ei.Attribute.Append("Namespace"), "xs:string", address.NamespaceUri);
        return ei;
    }
}

/// <summary>The server element a DataVariable points to.</summary>
public sealed record DataSource(string? EndpointUrl, string? DiscoveryUrl, IReadOnlyList<string> NamespaceTable, InternalElementType Element);

public static class BprDataVariable
{
    /// <summary>
    /// Reads a DataVariable: its NodeId resolved against the NameSpaceTable of
    /// the server element named in RefDataSource.
    /// </summary>
    public static (UaNodeAddress Address, DataSource Source) Read(AttributeType dataVariable, CAEXDocument doc)
    {
        var nodeIdText = AnnexANodeId.Value(Sub(dataVariable, "NodeId"))
            ?? throw new AddressingException($"'{dataVariable.Name}' has no NodeId value.");
        var sourceId = AnnexANodeId.Value(Sub(dataVariable, "RefDataSource"))
            ?? throw new AddressingException($"'{dataVariable.Name}' has no RefDataSource.");
        var server = doc.FindByID(sourceId, true, null) as InternalElementType
            ?? throw new AddressingException($"'{dataVariable.Name}': data source '{sourceId}' not found.");
        var source = SourceOf(server);
        return (UaNodeAddress.Parse(nodeIdText, source.NamespaceTable), source);
    }

    public static DataSource SourceOf(InternalElementType server)
    {
        var table = new List<string>();
        if (server.Attribute["NameSpaceTable"] is { } nst)
        {
            foreach (var entry in nst.Attribute.OrderBy(a => int.TryParse(a.Name, out var i) ? i : int.MaxValue))
            {
                if (!int.TryParse(entry.Name, out var index)) continue;
                while (table.Count < index) table.Add("");
                if (table.Count == index) table.Add(entry.Value ?? "");
            }
        }
        return new DataSource(AnnexANodeId.Value(server.Attribute["EndpointURL"]),
            AnnexANodeId.Value(server.Attribute["DiscoveryURL"]), table, server);
    }

    /// <summary>
    /// Writes a DataVariable attribute. The index is taken from the server's
    /// NameSpaceTable, where the namespace URI is appended if missing.
    /// </summary>
    public static AttributeType Write(IObjectWithAttributes owner, string name, UaNodeAddress address, InternalElementType server)
    {
        var nst = server.Attribute["NameSpaceTable"] ?? server.Attribute.Append("NameSpaceTable");
        var table = SourceOf(server).NamespaceTable.ToList();
        if (!table.Contains(address.NamespaceUri))
        {
            if (table.Count == 0)
            {
                AnnexANodeId.Set(nst.Attribute.Append("0"), "xs:anyURI", "http://opcfoundation.org/UA/");
                table.Add("http://opcfoundation.org/UA/");
            }
            if (address.NamespaceUri != table[0])
            {
                AnnexANodeId.Set(nst.Attribute.Append(table.Count.ToString()), "xs:anyURI", address.NamespaceUri);
                table.Add(address.NamespaceUri);
            }
        }

        var dv = owner.Attribute.Append(name);
        var semantic = dv.RefSemantic.Append();
        semantic.CorrespondingAttributePath = "aml-dataSourceType:OPCUA";
        dv.Attribute.Append("NodeId").Value = address.ToNodeIdString(table);
        dv.Attribute.Append("RefDataSource").Value = server.ID;
        return dv;
    }

    public static bool IsDataVariable(AttributeType a) =>
        a.RefSemantic.Any(s => s.CorrespondingAttributePath is "aml-dataSourceType:OPCUA" or "aml-accessPath:OPCUA")
        || (Sub(a, "NodeId") != null && Sub(a, "RefDataSource") != null);

    private static AttributeType? Sub(AttributeType a, string name) =>
        a.Attribute.FirstOrDefault(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase));
}
