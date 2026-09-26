// Takes the instances a model holds into a document, after its libraries: the
// topmost elements of the converted Objects folder that belong to the model's
// namespace, with everything below them. The instances of the base models (the
// Server object, DI's folders) stay behind.
//
// Into a hierarchy of their own, named after the model, the instances keep the
// IDs Opc2Aml derived from their NodeIds, and a second Apply replaces that
// hierarchy. Below an element the user chose, every Apply adds a copy, so each
// copy gets IDs of its own and its links are rewired to them. Links that lead
// out of what is taken (to the Objects folder, to another model's instances)
// are left out and counted.

using System.Xml.Linq;
using Aml.Engine.CAEX;
using Aml.Engine.CAEX.Extensions;

namespace OpcUaAml.Import;

public abstract record InstanceDestination
{
    /// <summary>An InstanceHierarchy of its own, replaced when it exists.</summary>
    public sealed record OwnHierarchy(string Name) : InstanceDestination;

    /// <summary>Below an element of the document, added to what it holds.</summary>
    public sealed record Below(IInternalElementContainer Parent) : InstanceDestination;
}

public sealed record InstanceTransferResult(int Taken, int Elements, int LinksLeftOut, string Where)
{
    public override string ToString() =>
        $"{Taken} instance(s) with {Elements} element(s) taken into {Where}"
        + (LinksLeftOut > 0 ? $", {LinksLeftOut} link(s) to nodes outside left out" : "");
}

public static class InstanceTransfer
{
    /// <summary>"IH_" and the last part of the model URI: IH_MPS500 for http://hsu-hh.de/UA/MPS500/.</summary>
    public static string HierarchyName(string modelUri)
    {
        var last = modelUri.TrimEnd('/', ':').Split('/', ':').LastOrDefault(p => p.Length > 0) ?? "Model";
        return "IH_" + last;
    }

    /// <summary>The topmost instances of the model in a converted document.</summary>
    public static IReadOnlyList<InternalElementType> TopInstances(CAEXDocument converted, string modelUri) =>
        converted.CAEXFile.InstanceHierarchy
            .SelectMany(h => h.InternalElement.SelectMany(e => e.Descendants<InternalElementType>().Prepend(e)))
            .Where(e => NamespaceOf(e) == modelUri && (e.CAEXParent is not InternalElementType parent || NamespaceOf(parent) != modelUri))
            .ToList();

    public static InstanceTransferResult Take(CAEXDocument target, CAEXDocument converted, string modelUri,
        InstanceDestination destination, MergeOptions? options = null)
    {
        options ??= new MergeOptions();
        var elsewhere = new LibraryMerger.Elsewhere(ExternalLibraries.Read(target, options.DocumentFolder), converted);
        var tops = TopInstances(converted, modelUri);
        var copies = tops.Select(t => elsewhere.Rewrite((InternalElementType)t.Copy(deepCopy: true, assignNewIDs: false))).ToList();

        var fresh = destination is InstanceDestination.Below;
        var leftOut = 0;
        foreach (var copy in copies)
        {
            if (fresh) Renumber(copy.Node);
            leftOut += RemoveLinksLeadingOut(copy.Node);
        }

        string where;
        IInternalElementContainer into;
        switch (destination)
        {
            case InstanceDestination.OwnHierarchy own:
                var existing = target.CAEXFile.InstanceHierarchy.FirstOrDefault(h => h.Name == own.Name);
                var index = existing != null ? target.CAEXFile.InstanceHierarchy.ToList().IndexOf(existing) : -1;
                existing?.Remove();
                var ih = target.CAEXFile.InstanceHierarchy.Append(own.Name);
                if (index >= 0 && index < target.CAEXFile.InstanceHierarchy.Count - 1)
                {
                    ih.Remove();
                    target.CAEXFile.InstanceHierarchy.InsertAt(index, ih);
                    ih = target.CAEXFile.InstanceHierarchy.ElementAt(index);
                }
                into = ih;
                where = own.Name;
                break;
            case InstanceDestination.Below below:
                into = below.Parent;
                where = ((CAEXObject)below.Parent).Name;
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(destination));
        }

        foreach (var copy in copies)
        {
            copy.Name = UniqueName(into, copy.Name);
            into.InternalElement.Insert(copy, asFirst: false, asIs: true);
        }
        var elements = copies.Sum(c => c.Node.DescendantsAndSelf().Count(e => e.Name.LocalName == "InternalElement"));
        return new InstanceTransferResult(copies.Count, elements, leftOut, where);
    }

    private static string? NamespaceOf(InternalElementType element) =>
        element.Attribute["NodeId"]?.Attribute["RootNodeId"]?.Attribute["NamespaceUri"]?.Value;

    private static string UniqueName(IInternalElementContainer parent, string name)
    {
        var taken = parent.InternalElement.Select(e => e.Name).ToHashSet(StringComparer.Ordinal);
        if (!taken.Contains(name)) return name;
        for (var i = 2; ; i++)
            if (!taken.Contains($"{name} ({i})")) return $"{name} ({i})";
    }

    /// <summary>New IDs for every element and interface of a copy, and its links rewired to them.</summary>
    private static void Renumber(XElement copy)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var id in copy.DescendantsAndSelf().Attributes().Where(a => a.Name.LocalName == "ID"))
        {
            var fresh = Guid.NewGuid().ToString();
            map[id.Value] = fresh;
            id.Value = fresh;
        }
        foreach (var side in PartnerSides(copy))
            if (Partner(side.Value) is { } partner && map.TryGetValue(partner, out var fresh))
                side.Value = side.Value.Length == partner.Length ? fresh : fresh + side.Value[partner.Length..];
    }

    /// <summary>Removes the links of a copy whose other end is not in it; returns how many.</summary>
    private static int RemoveLinksLeadingOut(XElement copy)
    {
        var ids = copy.DescendantsAndSelf().Attributes().Where(a => a.Name.LocalName == "ID").Select(a => a.Value).ToHashSet(StringComparer.Ordinal);
        var outward = copy.Descendants().Where(e => e.Name.LocalName == "InternalLink")
            .Where(l => new[] { "RefPartnerSideA", "RefPartnerSideB" }
                .Any(n => Partner((string?)l.Attribute(n)) is not { } p || !ids.Contains(p)))
            .ToList();
        foreach (var link in outward) link.Remove();
        return outward.Count;
    }

    private static IEnumerable<XAttribute> PartnerSides(XElement copy) =>
        copy.Descendants().Where(e => e.Name.LocalName == "InternalLink")
            .SelectMany(l => l.Attributes().Where(a => a.Name.LocalName is "RefPartnerSideA" or "RefPartnerSideB"));

    /// <summary>The ID a link side names: the interface's ID (CAEX 3.0), or the element's before ':' (CAEX 2.15).</summary>
    private static string? Partner(string? side)
    {
        if (string.IsNullOrEmpty(side)) return null;
        var colon = side.IndexOf(':');
        return colon > 0 ? side[..colon] : side;
    }
}
