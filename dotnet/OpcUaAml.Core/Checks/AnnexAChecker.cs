// Checks instances in a document against the UA types they claim to be, as
// far as OPC 10000-83 Annex A expresses them. Only the instance hierarchies
// are checked: the libraries are generated and trusted.

using Aml.Engine.CAEX;
using Aml.Engine.CAEX.Extensions;
using OpcUaAml.Types;

namespace OpcUaAml.Checks;

public enum Severity { Error, Warning }

public sealed record Finding(string Rule, Severity Severity, string ElementPath, string Message, string? ElementId)
{
    public override string ToString() => $"{Severity.ToString().ToUpperInvariant(),-7} {Rule} {ElementPath}: {Message}";
}

/// <summary>The rules, with the text a report shows for them.</summary>
public static class Rules
{
    public const string UnknownType = "UA001";
    public const string AbstractType = "UA002";
    public const string MissingMandatory = "UA003";
    public const string MissingMandatoryPlaceholder = "UA004";
    public const string DanglingLink = "UA005";
    public const string WrongLinkPair = "UA006";
    public const string PlaceholderInInstance = "UA007";

    public static readonly IReadOnlyDictionary<string, string> Descriptions = new Dictionary<string, string>
    {
        [UnknownType] = "The element refers to a UA type that the document does not contain.",
        [AbstractType] = "The element instantiates an abstract UA type.",
        [MissingMandatory] = "A Mandatory child of the type is missing.",
        [MissingMandatoryPlaceholder] = "A MandatoryPlaceholder of the type has no instance.",
        [DanglingLink] = "An InternalLink points to an interface that does not exist.",
        [WrongLinkPair] = "An InternalLink connects reference interfaces that do not belong together (RefClassConnectsToPath).",
        [PlaceholderInInstance] = "A placeholder declaration (<Name>) was copied into an instance.",
    };
}

public static class AnnexAChecker
{
    public static IReadOnlyList<Finding> Check(CAEXDocument doc)
    {
        var findings = new List<Finding>();
        var interfaces = new Dictionary<string, ExternalInterfaceType>(StringComparer.Ordinal);
        foreach (var ih in doc.CAEXFile.InstanceHierarchy)
            foreach (var ei in ih.Descendants<ExternalInterfaceType>())
                if (!string.IsNullOrEmpty(ei.ID)) interfaces[ei.ID] = ei;

        foreach (var ih in doc.CAEXFile.InstanceHierarchy)
        {
            foreach (var ie in ih.Descendants<InternalElementType>())
            {
                CheckElement(doc, ie, findings);
                CheckLinks(doc, ie, interfaces, findings);
            }
        }
        return findings;
    }

    private static void CheckElement(CAEXDocument doc, InternalElementType ie, List<Finding> findings)
    {
        var path = PathOf(ie);
        if (ie.Name.Length > 2 && ie.Name[0] == '<' && ie.Name[^1] == '>')
            findings.Add(new Finding(Rules.PlaceholderInInstance, Severity.Warning, path,
                "placeholder declarations stand for any number of children; replace it by concrete ones or remove it.", ie.ID));

        if (!UaTypes.IsUaLibraryPath(ie.RefBaseSystemUnitPath)) return;
        var type = UaTypes.Resolve(doc, ie.RefBaseSystemUnitPath);
        if (type == null)
        {
            findings.Add(new Finding(Rules.UnknownType, Severity.Error, path,
                $"type '{ie.RefBaseSystemUnitPath}' not found; import the NodeSet that defines it.", ie.ID));
            return;
        }
        if (UaTypes.IsAbstract(type))
            findings.Add(new Finding(Rules.AbstractType, Severity.Error, path, $"'{type.Name}' is abstract.", ie.ID));

        var children = ie.InternalElement.ToList();
        foreach (var decl in UaTypes.Declarations(type))
        {
            switch (decl.Rule)
            {
                case ModellingRule.Mandatory when children.All(c => c.Name != decl.Name):
                    findings.Add(new Finding(Rules.MissingMandatory, Severity.Error, path,
                        $"Mandatory child '{decl.Name}' of '{type.Name}' is missing.", ie.ID));
                    break;
                case ModellingRule.MandatoryPlaceholder when !HasInstanceOf(doc, children, decl):
                    findings.Add(new Finding(Rules.MissingMandatoryPlaceholder, Severity.Error, path,
                        $"'{type.Name}' requires at least one child for '{decl.Name}'" +
                        (decl.TypePath != null ? $" of type {Short(decl.TypePath)}" : "") + ".", ie.ID));
                    break;
            }
        }
    }

    /// <summary>A child that instantiates the placeholder's type or a subtype of it.</summary>
    private static bool HasInstanceOf(CAEXDocument doc, List<InternalElementType> children, ChildDeclaration decl)
    {
        var wanted = UaTypes.Resolve(doc, decl.TypePath);
        return children.Any(c =>
        {
            if (c.Name == decl.Name) return false;
            if (wanted == null) return c.RefBaseSystemUnitPath == decl.TypePath;
            var t = UaTypes.Resolve(doc, c.RefBaseSystemUnitPath);
            return t != null && UaTypes.DerivesFrom(t, wanted);
        });
    }

    private static void CheckLinks(CAEXDocument doc, InternalElementType owner,
        Dictionary<string, ExternalInterfaceType> interfaces, List<Finding> findings)
    {
        foreach (var link in owner.InternalLink)
        {
            var a = Find(interfaces, link.RefPartnerSideA);
            var b = Find(interfaces, link.RefPartnerSideB);
            var path = PathOf(owner) + "/" + link.Name;
            if (a == null || b == null)
            {
                findings.Add(new Finding(Rules.DanglingLink, Severity.Error, path,
                    $"side {(a == null ? "A" : "B")} ('{(a == null ? link.RefPartnerSideA : link.RefPartnerSideB)}') does not resolve.", owner.ID));
                continue;
            }

            // Annex A, Table A.8: RefClassConnectsToPath names the only class the
            // other end may have. Checked in both directions; a pair is fine if
            // either side's constraint is satisfied by the other, since a
            // symmetric reference names its own class.
            var allowedFromA = ConnectsTo(doc, a);
            var allowedFromB = ConnectsTo(doc, b);
            if (allowedFromA == null && allowedFromB == null) continue;
            var okA = allowedFromA == null || IsClassOrSubclass(doc, b.RefBaseClassPath, allowedFromA);
            var okB = allowedFromB == null || IsClassOrSubclass(doc, a.RefBaseClassPath, allowedFromB);
            if (!okA || !okB)
                findings.Add(new Finding(Rules.WrongLinkPair, Severity.Error, path,
                    $"'{Short(a.RefBaseClassPath)}' may only connect to '{Short(allowedFromA ?? "?")}', " +
                    $"but the other end is '{Short(b.RefBaseClassPath)}'.", owner.ID));
        }
    }

    private static ExternalInterfaceType? Find(Dictionary<string, ExternalInterfaceType> interfaces, string reference)
    {
        if (string.IsNullOrEmpty(reference)) return null;
        if (interfaces.TryGetValue(reference, out var ei)) return ei;
        // CAEX 2.15 style "ElementID:InterfaceName".
        var colon = reference.IndexOf(':');
        return colon > 0 ? interfaces.Values.FirstOrDefault(e =>
            e.CAEXParent is CAEXObject p && p.ID == reference[..colon] && e.Name == reference[(colon + 1)..]) : null;
    }

    /// <summary>RefClassConnectsToPath of the interface's class, inherited along its class chain.</summary>
    private static string? ConnectsTo(CAEXDocument doc, ExternalInterfaceType ei)
    {
        for (var cls = doc.FindByPath(ei.RefBaseClassPath) as InterfaceFamilyType;
             cls != null;
             cls = doc.FindByPath(cls.RefBaseClassPath) as InterfaceFamilyType)
        {
            var value = cls.Attribute["RefClassConnectsToPath"]?.Value;
            if (!string.IsNullOrEmpty(value)) return value;
        }
        return null;
    }

    private static bool IsClassOrSubclass(CAEXDocument doc, string classPath, string wanted)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var path = classPath; !string.IsNullOrEmpty(path) && seen.Add(path);
             path = (doc.FindByPath(path) as InterfaceFamilyType)?.RefBaseClassPath)
        {
            if (SamePath(path, wanted)) return true;
        }
        return false;
    }

    /// <summary>Paths are written both as "[Lib]/[Class]" and "Lib/Class".</summary>
    private static bool SamePath(string a, string b) => Normalize(a) == Normalize(b);

    private static string Normalize(string path) => path.Replace("[", "").Replace("]", "");

    private static string Short(string path)
    {
        var n = Normalize(path);
        var slash = n.LastIndexOf('/');
        return slash >= 0 ? n[(slash + 1)..] : n;
    }

    private static string PathOf(CAEXObject element)
    {
        var parts = new List<string>();
        for (CAEXBasicObject? o = element; o is CAEXObject c; o = c.CAEXParent as CAEXBasicObject)
        {
            parts.Add(c.Name);
            if (c is InstanceHierarchyType) break;
        }
        parts.Reverse();
        return string.Join("/", parts);
    }
}
