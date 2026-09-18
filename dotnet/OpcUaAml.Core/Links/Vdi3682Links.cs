// Links between a Formalised Process Description (VDI/VDE 3682) and OPC UA
// in one AML document:
//
// - a TechnicalResource to the UA object that represents the same resource
//   (refOpcUaObject),
// - a ProcessOperator to the UA method that executes the process (refOpcUaMethod).
//
// Both are object references of the AutomationML object reference attribute
// types (Drath, AutomationML e.V., 1.1.1-beta): attributes of type xs:IDREF
// derived from refObj, holding the ID of the referenced element. The two
// derived types live in a small library of this project; the base library is
// embedded unmodified, because the AutomationML Editor does not load
// libraries through a file reference.
//
// Whether the resource link should rather be a refBaseObj (same logical
// object, aspect to base) is left open: that depends on which hierarchy a
// model treats as the base, see docs/vdi3682.md.

using System.Reflection;
using System.Xml.Linq;
using Aml.Engine.CAEX;
using Aml.Engine.CAEX.Extensions;
using OpcUaAml.Diagram;
using OpcUaAml.Types;

namespace OpcUaAml.Links;

public enum FpdKind { TechnicalResource, ProcessOperator }

public sealed record FpdLink(InternalElementType Source, FpdKind Kind, string TargetId, InternalElementType? Target);

public sealed class LinkException : Exception
{
    public LinkException(string message) : base(message) { }
}

public static class Vdi3682Links
{
    public const string ObjectReferencesLib = "AutomationML_ObjectReferences_AttributeTypeLib";
    public const string OwnLib = "AMLOpcUa_ReferenceAttributeTypeLib";
    public const string RefOpcUaObject = "refOpcUaObject";
    public const string RefOpcUaMethod = "refOpcUaMethod";

    private const string ObjectReferencesFile = "AutomationML_ObjectReferences_AttributeTypeLib_AMLEd2_1.1.1-beta.aml";

    /// <summary>FPD elements of the given kind: by class path or role ending in FPD_TechnicalResource / FPD_ProcessOperator.</summary>
    public static IEnumerable<InternalElementType> FpdElements(CAEXDocument doc, FpdKind kind)
    {
        var suffix = kind == FpdKind.TechnicalResource ? "FPD_TechnicalResource" : "FPD_ProcessOperator";
        return doc.CAEXFile.InstanceHierarchy
            .SelectMany(ih => ih.Descendants<InternalElementType>())
            .Where(ie => EndsWith(ie.RefBaseSystemUnitPath, suffix)
                         || ie.SupportedRoleClass.Any(r => EndsWith(r.RefRoleClassPath, suffix))
                         || ie.RoleRequirements.Any(r => EndsWith(r.RefBaseRoleClassPath, suffix)));
    }

    /// <summary>UA elements a link of the given kind may point to: Objects for resources, Methods for operators.</summary>
    public static IEnumerable<InternalElementType> UaTargets(CAEXDocument doc, FpdKind kind) =>
        doc.CAEXFile.InstanceHierarchy
            .SelectMany(ih => ih.Descendants<InternalElementType>())
            .Where(ie => UaTypes.IsUaLibraryPath(ie.RefBaseSystemUnitPath) || ie.RefBaseSystemUnitPath?.Contains("UaMethodNodeClass") == true)
            .Where(ie => IsMethod(ie) == (kind == FpdKind.ProcessOperator))
            .Where(ie => kind == FpdKind.ProcessOperator || !IsVariable(ie));

    /// <summary>
    /// Sets the link on <paramref name="fpd"/> to <paramref name="ua"/>, replacing
    /// a previous one of the same kind, and makes sure the document holds the
    /// attribute types.
    /// </summary>
    public static AttributeType Link(InternalElementType fpd, InternalElementType ua, FpdKind kind)
    {
        var doc = fpd.CAEXDocument ?? throw new LinkException("The element is not part of a document.");
        if (!FpdElements(doc, kind).Any(e => e.ID == fpd.ID))
            throw new LinkException($"'{fpd.Name}' is not a {kind} of a VDI 3682 process description.");
        if (kind == FpdKind.ProcessOperator && !IsMethod(ua))
            throw new LinkException($"'{ua.Name}' is not a UA method; a ProcessOperator links to the method that executes it.");
        if (kind == FpdKind.TechnicalResource && (IsMethod(ua) || IsVariable(ua)))
            throw new LinkException($"'{ua.Name}' is not a UA object; a TechnicalResource links to the object that represents it.");
        if (string.IsNullOrEmpty(ua.ID)) throw new LinkException($"'{ua.Name}' has no ID to reference.");

        EnsureLibraries(doc.CAEXFile);
        var name = kind == FpdKind.TechnicalResource ? RefOpcUaObject : RefOpcUaMethod;
        if (fpd.Attribute[name] is { } old) fpd.Attribute.RemoveElement(old);
        var attr = fpd.Attribute.Append(name);
        attr.RefAttributeType = OwnLib + "/" + name;
        attr.AttributeDataType = "xs:IDREF";
        attr.Value = ua.ID;
        return attr;
    }

    /// <summary>Every link in the document, with its target resolved (null if the ID is gone).</summary>
    public static IReadOnlyList<FpdLink> Links(CAEXDocument doc)
    {
        var result = new List<FpdLink>();
        foreach (var kind in new[] { FpdKind.TechnicalResource, FpdKind.ProcessOperator })
        {
            var name = kind == FpdKind.TechnicalResource ? RefOpcUaObject : RefOpcUaMethod;
            foreach (var fpd in FpdElements(doc, kind))
            {
                var id = fpd.Attribute[name]?.Value;
                if (string.IsNullOrEmpty(id)) continue;
                result.Add(new FpdLink(fpd, kind, id, doc.FindByID(id, true, null) as InternalElementType));
            }
        }
        return result;
    }

    /// <summary>Adds the object reference types and this project's derived types if missing.</summary>
    public static void EnsureLibraries(CAEXFileType caex)
    {
        XNamespace ns = caex.Node.Name.Namespace;
        if (caex.AttributeTypeLib[ObjectReferencesLib] == null)
        {
            var lib = XDocument.Parse(ReadEmbedded(ObjectReferencesFile)).Root!
                .Elements().First(e => e.Name.LocalName == "AttributeTypeLib" && (string?)e.Attribute("Name") == ObjectReferencesLib);
            caex.Node.Add(Renamespace(lib, ns));
        }
        if (caex.AttributeTypeLib[OwnLib] == null)
        {
            var own = caex.AttributeTypeLib.Append(OwnLib);
            own.Description = "Object references between a VDI 3682 process description and OPC UA (AMLOpcUa).";
            own.Version = "1.0.0";
            Derived(own, RefOpcUaObject,
                "Attached to a VDI 3682 TechnicalResource. Its value references the OPC UA object (an element typed by an OPC 10000-83 Annex A class) that represents the same resource.");
            Derived(own, RefOpcUaMethod,
                "Attached to a VDI 3682 ProcessOperator. Its value references the OPC UA method whose call executes the process operator.");
        }
    }

    private static void Derived(AttributeTypeLibType lib, string name, string description)
    {
        var t = lib.AttributeType.Append(name);
        t.AttributeDataType = "xs:IDREF";
        t.RefAttributeType = ObjectReferencesLib + "/refObj";
        t.Description = description;
    }

    private static XElement Renamespace(XElement e, XNamespace ns) =>
        new(ns + e.Name.LocalName, e.Attributes(), e.Nodes().Select(n => n is XElement c ? Renamespace(c, ns) : n));

    private static bool IsMethod(InternalElementType ie) =>
        ie.RefBaseSystemUnitPath?.Contains("UaMethodNodeClass") == true
        || (UaTypes.TypeOf(ie) is { } t && UaTypes.Chain(t).Any(c => c.Name == "UaMethodNodeClass"));

    private static bool IsVariable(InternalElementType ie) =>
        UaTypes.TypeOf(ie) is { } t && UaTypes.Chain(t).Any(c => c.Name == "BaseVariableType");

    private static bool EndsWith(string? path, string suffix) =>
        path != null && DiagramBuilder.Segments(path).LastOrDefault() == suffix;

    private static string ReadEmbedded(string fileName)
    {
        var assembly = Assembly.GetExecutingAssembly();
        var name = assembly.GetManifestResourceNames().FirstOrDefault(n => n.EndsWith(fileName, StringComparison.Ordinal))
            ?? throw new InvalidOperationException($"'{fileName}' is not embedded in {assembly.GetName().Name}.");
        using var stream = assembly.GetManifestResourceStream(name)!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
