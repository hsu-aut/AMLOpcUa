// The BrowseNames of the standard nodes by NodeId ("i=47" is HasComponent).
// Taken from the OPC Foundation stack's own tables by reflection, so the list
// is exactly the one the stack knows and ages with it.

using System.Reflection;
using Opc.Ua;

namespace OpcUaAml.ModelDesign;

public static class UaNames
{
    private static readonly Lazy<Dictionary<uint, string>> Names = new(Build);

    /// <summary>The BrowseName of a standard node, or null when the id is none.</summary>
    public static string? Of(string nodeId)
    {
        var part = nodeId[(nodeId.IndexOf(';') + 1)..];
        return part.StartsWith("i=", StringComparison.Ordinal) && uint.TryParse(part[2..], out var id)
               && Names.Value.TryGetValue(id, out var name)
            ? name
            : null;
    }

    private static Dictionary<uint, string> Build()
    {
        var names = new Dictionary<uint, string>();
        Type[] tables =
        [
            typeof(ObjectIds), typeof(ObjectTypeIds), typeof(VariableIds), typeof(VariableTypeIds),
            typeof(DataTypeIds), typeof(ReferenceTypeIds), typeof(MethodIds),
        ];
        foreach (var table in tables)
        {
            foreach (var field in table.GetFields(BindingFlags.Public | BindingFlags.Static))
            {
                var identifier = field.GetValue(null) switch
                {
                    NodeId { IdType: IdType.Numeric, NamespaceIndex: 0 } id => (uint)id.Identifier,
                    ExpandedNodeId { IdType: IdType.Numeric, NamespaceIndex: 0, IsAbsolute: false } id => (uint)id.Identifier,
                    _ => (uint?)null,
                };
                if (identifier is { } value) names.TryAdd(value, field.Name);
            }
        }
        return names;
    }
}
