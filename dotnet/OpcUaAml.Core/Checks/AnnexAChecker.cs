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
    /// <summary>
    /// The template of <see cref="Message"/> (one of <see cref="FindingTexts"/>), with
    /// {0}, {1} … for <see cref="Args"/>: a report in another language translates the
    /// template and puts the names back in.
    /// </summary>
    public string? Format { get; init; }

    /// <summary>The names put into <see cref="Format"/>.</summary>
    public IReadOnlyList<string> Args { get; init; } = Array.Empty<string>();

    public override string ToString() => $"{Severity.ToString().ToUpperInvariant(),-7} {Rule} {ElementPath}: {Message}";

    internal static Finding Of(string rule, Severity severity, string path, string? elementId, string format, params string[] args) =>
        new(rule, severity, path, string.Format(System.Globalization.CultureInfo.InvariantCulture, format, args), elementId)
        {
            Format = format,
            Args = args,
        };
}

/// <summary>The templates of the findings' messages, in English, as the command line shows them.</summary>
public static class FindingTexts
{
    public const string BrokenVdi3682Link = "links to '{0}', which is not in the document.";
    public const string PlaceholderInInstance = "placeholder declarations stand for any number of children; replace it by concrete ones or remove it.";
    public const string UnknownType = "type '{0}' not found; import the NodeSet that defines it.";
    public const string AbstractType = "'{0}' is abstract.";
    public const string MissingMandatory = "Mandatory child '{0}' of '{1}' is missing.";
    public const string MissingMandatoryPlaceholder = "'{0}' requires at least one child for '{1}'.";
    public const string MissingMandatoryPlaceholderOfType = "'{0}' requires at least one child for '{1}' of type {2}.";
    public const string DanglingLink = "side {0} ('{1}') does not resolve.";
    public const string WrongLinkPair = "'{0}' may only connect to '{1}', but the other end is '{2}'.";

    public static readonly IReadOnlyList<string> All = new[]
    {
        BrokenVdi3682Link, PlaceholderInInstance, UnknownType, AbstractType, MissingMandatory,
        MissingMandatoryPlaceholder, MissingMandatoryPlaceholderOfType, DanglingLink, WrongLinkPair,
    };
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
    public const string BrokenVdi3682Link = "UA008";

    public static readonly IReadOnlyDictionary<string, string> Descriptions = new Dictionary<string, string>
    {
        [UnknownType] = "The element refers to a UA type that the document does not contain.",
        [AbstractType] = "The element instantiates an abstract UA type.",
        [MissingMandatory] = "A Mandatory child of the type is missing.",
        [MissingMandatoryPlaceholder] = "A MandatoryPlaceholder of the type has no instance.",
        [DanglingLink] = "An InternalLink points to an interface that does not exist.",
        [WrongLinkPair] = "An InternalLink connects reference interfaces that do not belong together (RefClassConnectsToPath).",
        [PlaceholderInInstance] = "A placeholder declaration (<Name>) was copied into an instance.",
        [BrokenVdi3682Link] = "A VDI 3682 element links to an OPC UA element that does not exist.",
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

        // The declarations of a type, worked out once per type.
        var declarations = new Dictionary<string, IReadOnlyList<ChildDeclaration>>(StringComparer.Ordinal);
        foreach (var ih in doc.CAEXFile.InstanceHierarchy)
            foreach (var top in ih.InternalElement)
                Walk(top, null);
        foreach (var link in Links.Vdi3682Links.Links(doc).Where(l => l.Target == null))
            findings.Add(Finding.Of(Rules.BrokenVdi3682Link, Severity.Warning, PathOf(link.Source), link.Source.ID,
                FindingTexts.BrokenVdi3682Link, link.TargetId));
        return findings;

        // Down the hierarchy, so that what a type declares below its children
        // is checked on them too, not only what their own types declare.
        void Walk(InternalElementType ie, IReadOnlyList<ChildDeclaration>? declared)
        {
            var applying = CheckElement(doc, ie, declared, declarations, findings);
            CheckLinks(doc, ie, interfaces, findings);
            foreach (var child in ie.InternalElement)
                Walk(child, UaTypes.Below(applying, child));
        }
    }

    /// <summary>Checks one element; returns the declarations that apply to it.</summary>
    /// <param name="declared">What the parent's type declares below the element, if anything.</param>
    private static IReadOnlyList<ChildDeclaration> CheckElement(CAEXDocument doc, InternalElementType ie,
        IReadOnlyList<ChildDeclaration>? declared, Dictionary<string, IReadOnlyList<ChildDeclaration>> declarations, List<Finding> findings)
    {
        var path = PathOf(ie);
        if (ie.Name.Length > 2 && ie.Name[0] == '<' && ie.Name[^1] == '>')
            findings.Add(Finding.Of(Rules.PlaceholderInInstance, Severity.Warning, path, ie.ID, FindingTexts.PlaceholderInInstance));

        SystemUnitFamilyType? type = null;
        IReadOnlyList<ChildDeclaration> own = Array.Empty<ChildDeclaration>();
        if (UaTypes.IsUaLibraryPath(ie.RefBaseSystemUnitPath))
        {
            type = UaTypes.Resolve(doc, ie.RefBaseSystemUnitPath);
            if (type == null)
            {
                findings.Add(Finding.Of(Rules.UnknownType, Severity.Error, path, ie.ID, FindingTexts.UnknownType, ie.RefBaseSystemUnitPath));
            }
            else
            {
                if (UaTypes.IsAbstract(type))
                    findings.Add(Finding.Of(Rules.AbstractType, Severity.Error, path, ie.ID, FindingTexts.AbstractType, type.Name));
                if (!declarations.TryGetValue(ie.RefBaseSystemUnitPath, out own!))
                    declarations[ie.RefBaseSystemUnitPath] = own = UaTypes.Declarations(type);
            }
        }
        var applying = UaTypes.Merge(declared ?? Array.Empty<ChildDeclaration>(), own);
        var fromOwnType = own.ToHashSet(ReferenceEqualityComparer.Instance);

        var children = ie.InternalElement.ToList();
        foreach (var decl in applying)
        {
            // Named after the element's type, or after the type that declares
            // the child below the element.
            var owner = type != null && fromOwnType.Contains(decl) ? type.Name : UaTypes.DeclaringType(decl)?.Name ?? ie.Name;
            switch (decl.Rule)
            {
                case ModellingRule.Mandatory when children.All(c => c.Name != decl.Name):
                    findings.Add(Finding.Of(Rules.MissingMandatory, Severity.Error, path, ie.ID, FindingTexts.MissingMandatory, decl.Name, owner));
                    break;
                case ModellingRule.MandatoryPlaceholder when !HasInstanceOf(doc, children, decl):
                    findings.Add(decl.TypePath != null
                        ? Finding.Of(Rules.MissingMandatoryPlaceholder, Severity.Error, path, ie.ID, FindingTexts.MissingMandatoryPlaceholderOfType, owner, decl.Name, Short(decl.TypePath))
                        : Finding.Of(Rules.MissingMandatoryPlaceholder, Severity.Error, path, ie.ID, FindingTexts.MissingMandatoryPlaceholder, owner, decl.Name));
                    break;
            }
        }
        return applying;
    }

    /// <summary>A child that instantiates the placeholder's type or a subtype of it.</summary>
    private static bool HasInstanceOf(CAEXDocument doc, List<InternalElementType> children, ChildDeclaration decl)
    {
        var wanted = UaTypes.Resolve(doc, decl.TypePath);
        return children.Any(c =>
        {
            // The placeholder itself may still sit in the instance; a child
            // that carries a placeholder rule of its own is not a filled one.
            // The name says nothing: an object or variable is filled under a
            // name of its own, while a method keeps the name its declaration
            // gives it (OPC 10000-3 6.4.4.4). A child another declaration
            // stands for counts as well: 6.4.4.4.5 asks for an instance of the
            // type, not for one no other declaration names.
            if (UaTypes.RuleOf(c) is ModellingRule.MandatoryPlaceholder or ModellingRule.OptionalPlaceholder) return false;
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
                findings.Add(Finding.Of(Rules.DanglingLink, Severity.Error, path, owner.ID, FindingTexts.DanglingLink,
                    a == null ? "A" : "B", a == null ? link.RefPartnerSideA : link.RefPartnerSideB));
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
                findings.Add(Finding.Of(Rules.WrongLinkPair, Severity.Error, path, owner.ID, FindingTexts.WrongLinkPair,
                    Short(a.RefBaseClassPath), Short(allowedFromA ?? "?"), Short(b.RefBaseClassPath)));
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
