// Current values from a running server into the document, for everything
// that is bound to a node: elements with an Annex A NodeId (their Value
// attribute), BPR DataVariables (the variable attribute itself) and the older
// "aml-opcua-variable" binding of Kühnert et al. (its parent attribute). The result
// is a document "as is" at one point in time; nothing is subscribed.

using Aml.Engine.CAEX;
using Aml.Engine.CAEX.Extensions;
using OpcUaAml.Addressing;

namespace OpcUaAml.Server;

public sealed record SnapshotResult(int Read, int Failed, int Skipped, IReadOnlyList<string> Problems)
{
    public override string ToString() =>
        $"{Read} value(s) read, {Failed} failed, {Skipped} skipped";
}

public static class ValueSnapshot
{
    /// <summary>
    /// Reads every bound value in the instance hierarchies. Bindings to
    /// another server (a ServerInstanceUri or data source endpoint that
    /// differs) are skipped.
    /// </summary>
    public static async Task<SnapshotResult> ApplyAsync(CAEXDocument doc, UaClient client, CancellationToken ct = default)
    {
        var (targets, problems, skipped) = Targets(doc, client);

        var read = 0;
        var failed = 0;
        if (targets.Count > 0)
        {
            var results = await client.ReadManyAsync(targets.Select(t => t.Address).ToList(), ct).ConfigureAwait(false);
            for (var i = 0; i < results.Count; i++)
            {
                if (results[i].Good) { targets[i].Write(results[i]); read++; }
                else { failed++; problems.Add($"{targets[i].What}: {results[i].Status}"); }
            }
        }
        return new SnapshotResult(read, failed, skipped, problems);
    }

    /// <summary>A bound value in the document: how to write it, which node, and a name for messages.</summary>
    internal sealed record Target(Action<UaReadResult> Write, UaNodeAddress Address, string What);

    /// <summary>Every bound value of the instance hierarchies that this client's server serves.</summary>
    internal static (List<Target> Targets, List<string> Problems, int Skipped) Targets(CAEXDocument doc, UaClient client)
    {
        var targets = new List<Target>();
        var problems = new List<string>();
        var skipped = 0;

        foreach (var ie in doc.CAEXFile.InstanceHierarchy.SelectMany(ih => ih.Descendants<InternalElementType>()))
        {
            UaNodeAddress? address = null;
            try { address = AnnexANodeId.Of(ie); }
            catch (AddressingException ex) { problems.Add($"{ie.Name}: {ex.Message}"); }
            if (address != null && ie.Attribute["Value"] != null)
            {
                if (OtherServer(address.ServerUri, client)) skipped++;
                else targets.Add(new Target(r => SetValue(ie, r), address with { ServerUri = null }, ie.Name));
            }

            foreach (var legacy in AllAttributes(ie).Where(LegacyOpcUaVariable.IsBound))
            {
                try
                {
                    var (server, nodeId) = LegacyOpcUaVariable.Read(legacy);
                    if (!SameUrl(server, client)) { skipped++; continue; }
                    // The index belongs to exactly this server, so its table resolves it.
                    targets.Add(new Target(r => SetAttribute(legacy, r), UaNodeAddress.Parse(nodeId, client.NamespaceTable), $"{ie.Name}/{legacy.Name}"));
                }
                catch (Exception ex) when (ex is AddressingException or FormatException)
                {
                    problems.Add($"{ie.Name}/{legacy.Name}: {ex.Message}");
                }
            }

            foreach (var dv in ie.Attribute.Where(BprDataVariable.IsDataVariable))
            {
                try
                {
                    var (a, source) = BprDataVariable.Read(dv, doc);
                    if (!SameEndpoint(source, client)) { skipped++; continue; }
                    targets.Add(new Target(r => SetAttribute(dv, r), a, $"{ie.Name}/{dv.Name}"));
                }
                catch (Exception ex) when (ex is AddressingException or FormatException)
                {
                    problems.Add($"{ie.Name}/{dv.Name}: {ex.Message}");
                }
            }
        }
        return (targets, problems, skipped);
    }

    /// <summary>Writes a value into the element's Value attribute, creating it if needed.</summary>
    public static void SetValue(InternalElementType element, UaReadResult result)
    {
        var value = element.Attribute["Value"] ?? element.Attribute.Append("Value");
        SetAttribute(value, result);
    }

    private static void SetAttribute(AttributeType attribute, UaReadResult result)
    {
        attribute.Value = result.ValueText;
        if (string.IsNullOrEmpty(attribute.AttributeDataType) && XsdType(result.DataType) is { } xs)
            attribute.AttributeDataType = xs;
    }

    private static string? XsdType(string? builtIn) => builtIn switch
    {
        "Boolean" => "xs:boolean",
        "SByte" => "xs:byte",
        "Byte" => "xs:unsignedByte",
        "Int16" => "xs:short",
        "UInt16" => "xs:unsignedShort",
        "Int32" => "xs:int",
        "UInt32" => "xs:unsignedInt",
        "Int64" => "xs:long",
        "UInt64" => "xs:unsignedLong",
        "Float" => "xs:float",
        "Double" => "xs:double",
        "String" or "LocalizedText" or "QualifiedName" => "xs:string",
        "DateTime" => "xs:dateTime",
        "ByteString" => "xs:base64Binary",
        _ => null,
    };

    private static bool OtherServer(string? serverUri, UaClient client) =>
        serverUri != null && client.ServerUri != null && serverUri != client.ServerUri;

    private static bool SameEndpoint(DataSource source, UaClient client) => source.Url == null || SameUrl(source.Url, client);

    private static bool SameUrl(string url, UaClient client) => Normalize(url) == Normalize(client.EndpointUrl);

    private static string Normalize(string u) => u.TrimEnd('/').ToLowerInvariant().Replace("://127.0.0.1", "://localhost");

    private static IEnumerable<AttributeType> AllAttributes(IObjectWithAttributes owner) =>
        owner.Attribute.SelectMany(a => new[] { a }.Concat(AllAttributes(a)));
}
