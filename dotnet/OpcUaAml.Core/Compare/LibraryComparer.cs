// Structural comparison of the class libraries of two CAEX documents.
//
// Used as a test oracle (our import against the libraries the OPC Foundation
// publishes) and for round trips. It compares what a modeller sees: which
// libraries and classes exist, where they derive from, which attributes,
// interfaces, roles and child elements they carry. IDs and document order are
// ignored because they carry no meaning across generator runs.

using Aml.Engine.CAEX;
using Aml.Engine.CAEX.Extensions;

namespace OpcUaAml.Compare;

public enum DifferenceKind { OnlyLeft, OnlyRight, Changed }

public sealed record Difference(DifferenceKind Kind, string Path, string Detail)
{
    public override string ToString() => Kind switch
    {
        DifferenceKind.OnlyLeft => $"- {Path}{(Detail.Length > 0 ? " " + Detail : "")}",
        DifferenceKind.OnlyRight => $"+ {Path}{(Detail.Length > 0 ? " " + Detail : "")}",
        _ => $"~ {Path}: {Detail}",
    };
}

public sealed class LibraryComparer
{
    /// <summary>Only libraries whose name passes this filter are compared.</summary>
    public Func<string, bool> LibraryFilter { get; init; } = _ => true;

    /// <summary>Compare attribute values, not only their presence and type.</summary>
    public bool CompareValues { get; init; } = true;

    /// <summary>
    /// Compare attributes, interfaces, roles and links. Off, only the class
    /// skeleton is compared: libraries, classes, derivation, child elements.
    /// </summary>
    public bool CompareFacets { get; init; } = true;

    public IReadOnlyList<Difference> Compare(CAEXDocument left, CAEXDocument right)
    {
        var l = Flatten(left);
        var r = Flatten(right);
        var diffs = new List<Difference>();

        foreach (var (path, lv) in l)
        {
            if (!r.TryGetValue(path, out var rv))
                diffs.Add(new Difference(DifferenceKind.OnlyLeft, path, ""));
            else if (lv != rv)
                diffs.Add(new Difference(DifferenceKind.Changed, path, $"'{lv}' vs '{rv}'"));
        }
        foreach (var (path, _) in r)
        {
            if (!l.ContainsKey(path))
                diffs.Add(new Difference(DifferenceKind.OnlyRight, path, ""));
        }
        return diffs.OrderBy(d => d.Path, StringComparer.Ordinal).ToList();
    }

    /// <summary>Counts per kind and per facet (the last path segment's tag).</summary>
    public static IReadOnlyList<(string Key, int Count, Difference Example)> Summarize(IEnumerable<Difference> diffs) =>
        diffs.GroupBy(d => $"{d.Kind} {d.Path[..d.Path.IndexOf(':')]} {Facet(d.Path)}")
             .Select(g => (g.Key, g.Count(), g.First()))
             .OrderByDescending(x => x.Item2)
             .ToList();

    private static string Facet(string path)
    {
        var at = path.LastIndexOf("/@", StringComparison.Ordinal);
        if (at < 0) return "class";
        var tag = path[(at + 2)..];
        var colon = tag.IndexOf(':');
        return colon < 0 ? tag : tag[..colon] + ":" + tag[(colon + 1)..].Split('/')[0];
    }

    /// <summary>
    /// Every comparable fact of the document as path → value. A path names the
    /// library, the class chain and the facet, e.g.
    /// <c>SUC:[SUC_x]/DeviceType/@attr:SerialNumber:type</c>.
    /// </summary>
    public Dictionary<string, string> Flatten(CAEXDocument doc)
    {
        var facts = new Dictionary<string, string>(StringComparer.Ordinal);
        var file = doc.CAEXFile;

        foreach (var lib in file.AttributeTypeLib.Where(x => LibraryFilter(x.Name)))
        {
            var p = $"ATL:{lib.Name}";
            facts[p] = "";
            foreach (var at in lib.AttributeType) AddAttributeType(facts, p, at);
        }
        foreach (var lib in file.InterfaceClassLib.Where(x => LibraryFilter(x.Name)))
        {
            var p = $"ICL:{lib.Name}";
            facts[p] = "";
            foreach (var ic in lib.InterfaceClass) AddInterfaceClass(facts, p, ic);
        }
        foreach (var lib in file.RoleClassLib.Where(x => LibraryFilter(x.Name)))
        {
            var p = $"RCL:{lib.Name}";
            facts[p] = "";
            foreach (var rc in lib.RoleClass) AddRoleClass(facts, p, rc);
        }
        foreach (var lib in file.SystemUnitClassLib.Where(x => LibraryFilter(x.Name)))
        {
            var p = $"SUC:{lib.Name}";
            facts[p] = "";
            foreach (var suc in lib.SystemUnitClass) AddSystemUnitClass(facts, p, suc);
        }
        return facts;
    }

    private void AddAttributeType(Dictionary<string, string> facts, string parent, AttributeFamilyType at)
    {
        var p = $"{parent}/{at.Name}";
        facts[p] = $"base={at.RefBaseClassPath} ref={at.RefAttributeType} type={at.AttributeDataType} unit={at.Unit}";
        foreach (var a in at.Attribute) AddAttribute(facts, p, a);
        foreach (var child in at.AttributeType) AddAttributeType(facts, p, child);
    }

    private void AddInterfaceClass(Dictionary<string, string> facts, string parent, InterfaceFamilyType ic)
    {
        var p = $"{parent}/{ic.Name}";
        facts[p] = $"base={ic.RefBaseClassPath}";
        foreach (var a in ic.Attribute) AddAttribute(facts, p, a);
        foreach (var ei in ic.ExternalInterface) AddInterface(facts, p, ei);
        foreach (var child in ic.InterfaceClass) AddInterfaceClass(facts, p, child);
    }

    private void AddRoleClass(Dictionary<string, string> facts, string parent, RoleFamilyType rc)
    {
        var p = $"{parent}/{rc.Name}";
        facts[p] = $"base={rc.RefBaseClassPath}";
        foreach (var a in rc.Attribute) AddAttribute(facts, p, a);
        foreach (var ei in rc.ExternalInterface) AddInterface(facts, p, ei);
        foreach (var child in rc.RoleClass) AddRoleClass(facts, p, child);
    }

    private void AddSystemUnitClass(Dictionary<string, string> facts, string parent, SystemUnitFamilyType suc)
    {
        var p = $"{parent}/{suc.Name}";
        facts[p] = $"base={suc.RefBaseClassPath}";
        AddSystemUnitContent(facts, p, suc);
        foreach (var child in suc.SystemUnitClass) AddSystemUnitClass(facts, p, child);
    }

    private void AddSystemUnitContent(Dictionary<string, string> facts, string p, SystemUnitClassType owner)
    {
        foreach (var a in owner.Attribute) AddAttribute(facts, p, a);
        foreach (var ei in owner.ExternalInterface) AddInterface(facts, p, ei);
        if (CompareFacets)
            foreach (var src in owner.SupportedRoleClass) facts[$"{p}/@role:{src.RefRoleClassPath}"] = "";
        if (CompareFacets)
            foreach (var link in owner.InternalLink)
            facts[$"{p}/@link:{link.Name}"] = $"{Side(owner, link.RefPartnerSideA)} <-> {Side(owner, link.RefPartnerSideB)}";
        foreach (var ie in owner.InternalElement)
        {
            var ip = $"{p}/{ie.Name}";
            facts[ip] = $"suc={ie.RefBaseSystemUnitPath}";
            AddSystemUnitContent(facts, ip, ie);
        }
    }

    private void AddInterface(Dictionary<string, string> facts, string parent, ExternalInterfaceType ei)
    {
        if (!CompareFacets) return;
        var p = $"{parent}/@if:{ei.Name}";
        facts[p] = $"class={ei.RefBaseClassPath}";
        foreach (var a in ei.Attribute) AddAttribute(facts, p, a);
    }

    private void AddAttribute(Dictionary<string, string> facts, string parent, AttributeType a)
    {
        if (!CompareFacets) return;
        var p = $"{parent}/@attr:{a.Name}";
        var value = CompareValues ? $" value={a.Value} default={a.DefaultValue}" : "";
        facts[p] = $"type={a.AttributeDataType} base={a.RefAttributeType} unit={a.Unit}{value}";
        foreach (var child in a.Attribute) AddAttribute(facts, p, child);
    }

    /// <summary>
    /// An InternalLink side as a path relative to the owner. IDs differ between
    /// generator runs; the element and interface names do not.
    /// </summary>
    private static string Side(SystemUnitClassType owner, string reference)
    {
        var colon = reference.IndexOf(':');
        if (colon <= 0) return reference;
        var id = reference[..colon];
        var iface = reference[(colon + 1)..];
        var target = owner.CAEXDocument?.FindByID(id, true, null);
        return target is CAEXObject o ? $"{o.Name}:{iface}" : reference;
    }
}
