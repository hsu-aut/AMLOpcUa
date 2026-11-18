// The variables of a running server a model can be bound to: browsed from a
// start node (the Objects folder unless given), breadth first, so a node
// reached on two ways keeps its shortest path. Left out: the server's own
// nodes (the base namespace: Server object, diagnostics), methods, and the
// properties that describe a variable rather than being a value of the plant
// (EngineeringUnits, EURange, ...). The kind of each value is read in batches.

using OpcUaAml.Addressing;
using OpcUaAml.Server;

namespace OpcUaAml.Binding;

/// <summary>The variables found; Truncated when the limit stopped the search.</summary>
public sealed record CollectedVariables(IReadOnlyList<ServerVariable> Variables, bool Truncated)
{
    /// <summary>Nodes whose children could not be browsed: the list may lack what is below them.</summary>
    public int Unbrowsed { get; init; }
}

public static class ServerVariables
{
    private const string UaBase = "http://opcfoundation.org/UA/";
    private const int Batch = 500;

    /// <summary>Properties that describe a variable (OPC 10000-3, 10000-8), not values of the plant.</summary>
    private static readonly HashSet<string> Describing = new(StringComparer.Ordinal)
    {
        "EngineeringUnits", "EURange", "InstrumentRange", "ValuePrecision", "Definition",
        "EnumStrings", "EnumValues", "TrueState", "FalseState", "ValueAsText", "AxisScaleType",
    };

    public static async Task<CollectedVariables> CollectAsync(UaClient client, UaNodeAddress? start = null,
        int limit = 20000, int depth = 12, CancellationToken ct = default)
    {
        // A start node the server does not have (a typo) is an error, not a server without variables.
        if (start != null) await client.DescribeAsync(start, ct).ConfigureAwait(false);
        var found = new List<(UaNodeAddress Address, string[] Path)>();
        var seen = new HashSet<UaNodeAddress>();
        var unbrowsed = 0;
        var queue = new Queue<(UaNodeAddress? Node, string[] Path)>();
        queue.Enqueue((start, []));
        var truncated = false;
        while (queue.Count > 0 && !truncated)
        {
            ct.ThrowIfCancellationRequested();
            var (node, path) = queue.Dequeue();
            IReadOnlyList<UaBrowseItem> children;
            try { children = await client.BrowseAsync(node, ct).ConfigureAwait(false); }
            catch (Exception ex) when (ex is not OperationCanceledException) { unbrowsed++; continue; }
            foreach (var c in children)
            {
                if (c.NodeClass == "Method" || c.Address.NamespaceUri == UaBase || !seen.Add(c.Address)) continue;
                string[] here = [.. path, c.BrowseName];
                if (c.NodeClass == "Variable")
                {
                    if (Describing.Contains(c.BrowseName)) continue;
                    if (found.Count >= limit) { truncated = true; break; }
                    found.Add((c.Address, here));
                }
                if (here.Length < depth) queue.Enqueue((c.Address, here));
            }
        }

        var variables = new List<ServerVariable>(found.Count);
        for (var i = 0; i < found.Count; i += Batch)
        {
            var chunk = found.Skip(i).Take(Batch).ToList();
            IReadOnlyList<UaReadResult>? read = null;
            try { read = await client.ReadManyAsync(chunk.Select(f => f.Address).ToList(), ct).ConfigureAwait(false); }
            catch (Exception ex) when (ex is not OperationCanceledException) { /* kinds unknown: nothing is ruled out by kind */ }
            for (var k = 0; k < chunk.Count; k++)
                variables.Add(new ServerVariable(chunk[k].Address, chunk[k].Path, read?[k] is { Good: true } r ? r.DataType : null));
        }
        return new CollectedVariables(variables, truncated) { Unbrowsed = unbrowsed };
    }
}
