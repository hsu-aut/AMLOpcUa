// The AutomationML object reference attribute types (Drath, Nabizada,
// AutomationML e.V., 1.1.1-beta): attributes of type xs:IDREF whose value is
// the ID of another element. refBaseObj is attached to an aspect object and
// references its base object; both represent the same logical object and hold
// complementary information. The library is embedded unmodified and copied
// into a document on first use, because the AutomationML Editor does not load
// libraries through a file reference.

using System.Reflection;
using System.Xml.Linq;
using Aml.Engine.CAEX;

namespace OpcUaAml.Links;

public static class ObjectReferences
{
    public const string Lib = "AutomationML_ObjectReferences_AttributeTypeLib";
    public const string RefBaseObj = "refBaseObj";

    private const string LibFile = "AutomationML_ObjectReferences_AttributeTypeLib_AMLEd2_1.1.1-beta.aml";

    /// <summary>Adds the attribute type library to the document if it is missing.</summary>
    public static void EnsureLibrary(CAEXFileType caex)
    {
        if (caex.AttributeTypeLib[Lib] != null) return;
        XNamespace ns = caex.Node.Name.Namespace;
        var lib = XDocument.Parse(ReadEmbedded(LibFile)).Root!
            .Elements().First(e => e.Name.LocalName == "AttributeTypeLib" && (string?)e.Attribute("Name") == Lib);
        caex.Node.Add(Renamespace(lib, ns));
    }

    /// <summary>Marks <paramref name="aspect"/> as an aspect of <paramref name="baseObject"/>, replacing an earlier refBaseObj.</summary>
    public static AttributeType SetBase(InternalElementType aspect, InternalElementType baseObject)
    {
        if (string.IsNullOrEmpty(baseObject.ID)) throw new InvalidOperationException($"'{baseObject.Name}' has no ID to reference.");
        EnsureLibrary(aspect.CAEXDocument!.CAEXFile);
        if (aspect.Attribute[RefBaseObj] is { } old) aspect.Attribute.RemoveElement(old);
        var attr = aspect.Attribute.Append(RefBaseObj);
        attr.RefAttributeType = Lib + "/" + RefBaseObj;
        attr.AttributeDataType = "xs:IDREF";
        attr.Value = baseObject.ID;
        return attr;
    }

    /// <summary>The ID an element names as its base object, or null.</summary>
    public static string? BaseOf(InternalElementType element) =>
        element.Attribute[RefBaseObj]?.Value is { Length: > 0 } id ? id : null;

    private static XElement Renamespace(XElement e, XNamespace ns) =>
        new(ns + e.Name.LocalName, e.Attributes(), e.Nodes().Select(n => n is XElement c ? Renamespace(c, ns) : n));

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
