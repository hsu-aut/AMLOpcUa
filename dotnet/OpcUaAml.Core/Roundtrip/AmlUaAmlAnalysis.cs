// Chain B: an AML document out through the AML-UA-XSLT rules and back in
// through OPC 10000-83 Annex A. The export turns every AML class into an OPC
// UA type in a namespace per AML library; Annex A turns every UA type into a
// SystemUnitClass in a library per namespace. The analysis matches by name
// and asks, per AML concept, what arrives.

using System.Xml.Linq;

namespace OpcUaAml.Roundtrip;

public static class AmlUaAmlAnalysis
{
    private static readonly string[] ClassKinds = { "SystemUnitClass", "RoleClass", "InterfaceClass", "AttributeType" };
    private static readonly string[] LibraryKinds = { "SystemUnitClassLib", "RoleClassLib", "InterfaceClassLib", "AttributeTypeLib" };

    public static IReadOnlyList<Criterion> Analyze(XDocument original, XDocument roundtrip)
    {
        var o = original.Root!;
        var r = roundtrip.Root!;

        // Annex A takes the whole address space from Root into one hierarchy,
        // "OPC UA Instance Hierarchy"; an exported InstanceHierarchy comes back
        // as an element of that name somewhere below it.
        var hierarchies = new UaAmlUaAnalysis.Tally("InstanceHierarchies present, as element", "below Root/Objects");
        var elements = new UaAmlUaAnalysis.Tally("InternalElements present, by path");
        var elementTypes = new UaAmlUaAnalysis.Tally("Element keeps its SystemUnitClass");
        var elementAttributes = new UaAmlUaAnalysis.Tally("Element attributes present, by name");
        var elementValues = new UaAmlUaAnalysis.Tally("Element attribute values kept");
        var rtElements = Kids(r, "InstanceHierarchy").SelectMany(h => h.Descendants()).Where(e => e.Name.LocalName == "InternalElement").ToList();
        foreach (var ih in Kids(o, "InstanceHierarchy"))
        {
            var rih = rtElements.FirstOrDefault(e => Name(e) == Name(ih));
            hierarchies.Add(rih != null, Name(ih));
            var rtPaths = rih == null ? new Dictionary<string, XElement>()
                : ElementPaths(rih, Name(ih)).GroupBy(p => p.Path).ToDictionary(g => g.Key, g => g.First().Element);
            foreach (var (path, ie) in ElementPaths(ih, Name(ih)))
            {
                rtPaths.TryGetValue(path, out var rie);
                elements.Add(rie != null, path);
                if (rie == null) continue;
                if (LastSegment(Attr(ie, "RefBaseSystemUnitPath")) is { } type)
                    elementTypes.Add(LastSegment(Attr(rie, "RefBaseSystemUnitPath")) == type, $"{path} ({type} -> {LastSegment(Attr(rie, "RefBaseSystemUnitPath")) ?? "-"})");
                foreach (var a in Kids(ie, "Attribute"))
                {
                    var match = Kids(rie, "Attribute").FirstOrDefault(x => Name(x) == Name(a))
                                ?? Kids(rie, "InternalElement").FirstOrDefault(x => Name(x) == Name(a));
                    elementAttributes.Add(match != null, $"{path}.{Name(a)}");
                    if ((Text(a, "Value") ?? Text(a, "DefaultValue")) is { } v)
                        elementValues.Add(match != null && Values(match, null).Contains(v), $"{path}.{Name(a)} = {v}");
                }
            }
        }

        // Classes of the round trip, by the library name they came from:
        // Annex A names a library "<prefix>_<namespace URI>", the export names
        // the namespace "http://opcfoundation.org/UA/AML/<library>".
        var rtClasses = new Dictionary<(string Lib, string Name), XElement>();
        foreach (var lib in r.Elements().Where(e => LibraryKinds.Contains(e.Name.LocalName)))
        {
            var libName = Name(lib);
            var marker = libName.IndexOf(UaAmlUaAnalysis.AmlNamespace, StringComparison.Ordinal);
            if (marker < 0) continue;
            var original_ = libName[(marker + UaAmlUaAnalysis.AmlNamespace.Length)..];
            foreach (var cls in lib.Descendants().Where(e => ClassKinds.Contains(e.Name.LocalName)))
                rtClasses.TryAdd((original_, Name(cls)), cls);
        }

        var classes = new UaAmlUaAnalysis.Tally("Classes present, by library and name");
        var kind = new UaAmlUaAnalysis.Tally("Class keeps its kind");
        var inheritance = new UaAmlUaAnalysis.Tally("Base class kept");
        var ids = new UaAmlUaAnalysis.Tally("Class ID recoverable", "AML_ID");
        var attributes = new UaAmlUaAnalysis.Tally("Class attributes present, by name");
        var values = new UaAmlUaAnalysis.Tally("Attribute values kept", "Value or DefaultValue");
        var units = new UaAmlUaAnalysis.Tally("Attribute units kept");
        var interfaces = new UaAmlUaAnalysis.Tally("Class interfaces present, by name");
        var children = new UaAmlUaAnalysis.Tally("Class child elements present, by name");

        foreach (var lib in o.Elements().Where(e => LibraryKinds.Contains(e.Name.LocalName)))
        {
            foreach (var cls in lib.Descendants().Where(e => ClassKinds.Contains(e.Name.LocalName)))
            {
                var label = $"{Name(lib)}/{Name(cls)}";
                rtClasses.TryGetValue((Name(lib), Name(cls)), out var rc);
                classes.Add(rc != null, label);
                if (rc == null) continue;

                kind.Add(rc.Name.LocalName == cls.Name.LocalName, $"{label} ({cls.Name.LocalName} -> {rc.Name.LocalName})");
                var baseName = LastSegment(Attr(cls, "RefBaseClassPath") ?? Attr(cls, "RefAttributeType"));
                if (baseName != null)
                {
                    var rtBase = LastSegment(Attr(rc, "RefBaseClassPath") ?? Attr(rc, "RefAttributeType"));
                    inheritance.Add(rtBase == baseName, $"{label} ({baseName} -> {rtBase ?? "-"})");
                }
                if (Attr(cls, "ID") is { Length: > 0 } id)
                    ids.Add(Values(rc, "AML_ID").Any(v => Plain(v) == Plain(id)), label);

                foreach (var a in Kids(cls, "Attribute"))
                {
                    var match = Kids(rc, "Attribute").FirstOrDefault(x => Name(x) == Name(a))
                                ?? Kids(rc, "InternalElement").FirstOrDefault(x => Name(x) == Name(a));
                    attributes.Add(match != null, $"{label}.{Name(a)}");
                    var v = Text(a, "Value") ?? Text(a, "DefaultValue");
                    if (v != null)
                        values.Add(match != null && Values(match, null).Contains(v), $"{label}.{Name(a)} = {v}");
                    if (Attr(a, "Unit") is { Length: > 0 } unit)
                        units.Add(match != null && (Attr(match, "Unit") == unit || match.Descendants().Any(d => Attr(d, "Unit") == unit || d.Value == unit)),
                            $"{label}.{Name(a)} [{unit}]");
                }
                foreach (var ei in Kids(cls, "ExternalInterface"))
                    interfaces.Add(Kids(rc, "ExternalInterface").Concat(Kids(rc, "InternalElement")).Any(x => Name(x) == Name(ei)), $"{label}/{Name(ei)}");
                foreach (var ie in Kids(cls, "InternalElement"))
                    children.Add(Kids(rc, "InternalElement").Any(x => Name(x) == Name(ie)), $"{label}/{Name(ie)}");
            }
        }

        return new[] { hierarchies, elements, elementTypes, elementAttributes, elementValues, classes, kind, inheritance, ids, attributes, values, units, interfaces, children }
            .Select(t => t.ToCriterion()).ToList();
    }

    private static IEnumerable<XElement> Kids(XElement e, string local) => e.Elements().Where(x => x.Name.LocalName == local);

    private static string Name(XElement e) => (string?)e.Attribute("Name") ?? "";

    private static string? Attr(XElement e, string name) => (string?)e.Attribute(name);

    private static string? Text(XElement e, string local) => Kids(e, local).FirstOrDefault()?.Value is { Length: > 0 } v ? v : null;

    /// <summary>Values found on an element: its Value/DefaultValue, and those of a child attribute of the given name.</summary>
    private static IEnumerable<string> Values(XElement e, string? attributeName)
    {
        var scope = attributeName == null ? e.DescendantsAndSelf() : Kids(e, "Attribute").Where(a => Name(a) == attributeName)
            .Concat(Kids(e, "InternalElement").Where(a => Name(a) == attributeName)).SelectMany(a => a.DescendantsAndSelf());
        return scope.Where(d => d.Name.LocalName is "Value" or "DefaultValue").Select(d => d.Value);
    }

    private static string Plain(string id) => id.Trim('{', '}').ToLowerInvariant();

    private static IEnumerable<(string Path, XElement Element)> ElementPaths(XElement container, string prefix)
    {
        foreach (var ie in Kids(container, "InternalElement"))
        {
            var path = prefix + "/" + Name(ie);
            yield return (path, ie);
            foreach (var sub in ElementPaths(ie, path)) yield return sub;
        }
    }

    /// <summary>The last segment of a class path, in plain or CAEX 3.0 bracket notation.</summary>
    internal static string? LastSegment(string? path)
    {
        if (string.IsNullOrEmpty(path)) return null;
        if (path.EndsWith(']'))
        {
            var start = path.LastIndexOf('[');
            return start >= 0 ? path[(start + 1)..^1] : path;
        }
        var slash = path.LastIndexOf('/');
        return slash >= 0 ? path[(slash + 1)..] : path;
    }
}
