// Chain C: an OPC UA NodeSet through OPC 10000-83 Annex A into AML and back
// out through the inverse of Annex A. Unlike chain A the result is meant to be
// the same model, so the analysis compares node by node: every fact of the
// original's own nodes (NodeSetComparer.Flatten) is either kept with the same
// value or lost. A lost fact of a node the AML does not carry at all was lost
// on the way in (Annex A); the others are the inverse's own share.

using System.Xml.Linq;
using OpcUaAml.Compare;
using OpcUaAml.Export;

namespace OpcUaAml.Roundtrip;

public static class InverseAnalysis
{
    // Known is what Annex A itself drops from nodes it does carry (found by
    // reading the AML); the inverse cannot bring it back.
    private static readonly (string Name, Func<string, bool> Matches, string? Known)[] Kinds =
    {
        ("Nodes present, with their node class", s => s == "", null),
        ("BrowseName kept", s => s == "/@BrowseName", null),
        ("DisplayName kept", s => s == "/@DisplayName", null),
        ("Description kept", s => s == "/@Description", null),
        ("ParentNodeId kept", s => s == "/@ParentNodeId", null),
        ("DataType kept", s => s == "/@DataType", null),
        ("ValueRank and ArrayDimensions kept", s => s is "/@ValueRank" or "/@ArrayDimensions", null),
        ("IsAbstract, Symmetric, InverseName kept", s => s is "/@IsAbstract" or "/@Symmetric" or "/@InverseName", null),
        ("MethodDeclarationId kept", s => s == "/@MethodDeclarationId", null),
        ("Values kept", s => s == "/@Value", "Annex A leaves out empty strings and some values of base model structures"),
        ("DataType definitions kept (per field)", s => s.StartsWith("/definition", StringComparison.Ordinal), "Annex A keeps no descriptions of option set bits"),
        ("References kept", s => s.StartsWith("/ref:", StringComparison.Ordinal), "Annex A drops some references between declarations of different types"),
        ("Documentation kept", s => s == "/@Documentation", "Annex A carries no Documentation"),
    };

    public static IReadOnlyList<Criterion> Analyze(XDocument original, XElement aml, XDocument roundtrip, string modelUri)
    {
        var inAml = AnnexAInverse.NodeIdsIn(aml);
        var comparer = new NodeSetComparer();
        var left = comparer.Flatten(original);
        var right = comparer.Flatten(roundtrip);
        var prefix = $"node:nsu={modelUri};";
        var totals = Kinds.Select(k => (k.Name, Total: 0, Kept: 0, Lost: new List<string>(), NotInAml: 0)).ToArray();

        foreach (var (key, value) in left)
        {
            if (!key.StartsWith(prefix, StringComparison.Ordinal)) continue;
            var slash = key.IndexOf('/', prefix.Length);
            var node = slash < 0 ? key[5..] : key[5..slash];
            var suffix = slash < 0 ? "" : key[slash..];
            // A repeated fact ("#2") belongs to the kind of its first occurrence.
            var hash = suffix.IndexOf('#');
            var kindKey = hash < 0 ? suffix : suffix[..hash];
            var i = Array.FindIndex(Kinds, k => k.Matches(kindKey));
            if (i < 0) continue;
            var kept = right.TryGetValue(key, out var other) && other == value;
            totals[i].Total++;
            if (kept) { totals[i].Kept++; continue; }
            // A reference to a node the AML does not carry (an encoding, a dictionary) was lost on the way in as well.
            var target = kindKey.StartsWith("/ref:", StringComparison.Ordinal) ? kindKey[(kindKey.IndexOfAny(new[] { '>', '<' }) + 1)..] : null;
            if (!inAml.Contains(node) || (target != null && target.StartsWith("nsu=", StringComparison.Ordinal) && !inAml.Contains(target))) { totals[i].NotInAml++; continue; }
            totals[i].Lost.Add(key[5..] + (value.Length > 0 && value.Length < 80 ? $" ({value})" : ""));
        }
        // The examples list what the inverse lost; the note counts what never reached the AML.
        return totals.Select((t, i) =>
        {
            var notes = new List<string>();
            if (t.NotInAml > 0) notes.Add($"{t.NotInAml} of the lost belong to nodes Annex A did not carry into AML");
            if (t.Lost.Count > 0 && Kinds[i].Known is { } known) notes.Add(known);
            return new Criterion(t.Name, t.Total, t.Kept, t.Lost, notes.Count == 0 ? null : string.Join("; ", notes));
        }).ToList();
    }
}
