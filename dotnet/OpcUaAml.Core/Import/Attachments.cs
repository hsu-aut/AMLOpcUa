// A file attached to an element of the document, as AutomationML references
// any external document: an ExternalInterface of the class ExternalDataReference
// (AutomationMLInterfaceClassLib) with the file's URI (refURI) and MIME type.
//
// Beside an .aml document the file is named relative to it, so the two move
// together. For a container (.amlx) the AutomationML Editor, when it saves the
// container, offers to take every file such an interface names into it and
// then names the part instead; it finds a relative file only by chance (it
// resolves against its own working copy), so a container's attachment is
// named by its full path until the editor has taken it in.

using System.Globalization;
using Aml.Engine.AmlObjects;
using Aml.Engine.CAEX;
using Aml.Engine.CAEX.Extensions;

namespace OpcUaAml.Import;

public static class Attachments
{
    public const string ExternalDataReference = "AutomationMLInterfaceClassLib/AutomationMLBaseInterface/ExternalDataConnector/ExternalDataReference";
    private const string AttributeTypes = "AutomationMLBaseAttributeTypeLib";

    /// <summary>"Plant.amlx" at 10:41:12 on 19 November 2026 gives "Plant.events-20261119-104112.csv".</summary>
    public static string FileName(string documentPath, DateTime now) =>
        Path.GetFileNameWithoutExtension(documentPath) + ".events-" + now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + ".csv";

    /// <summary>The refURI for <paramref name="file"/>: relative beside an .aml document, its full path for a container.</summary>
    public static string UriFor(string documentPath, string file) =>
        AmlContainers.IsContainerPath(documentPath)
            ? new Uri(Path.GetFullPath(file)).AbsoluteUri
            : Uri.EscapeDataString(Path.GetRelativePath(Path.GetDirectoryName(Path.GetFullPath(documentPath)) ?? "", Path.GetFullPath(file)).Replace('\\', '/'))
                .Replace("%2F", "/", StringComparison.Ordinal);

    /// <summary>
    /// Attaches the file <paramref name="uri"/> names to <paramref name="owner"/>,
    /// as a new interface each time (a second "Events" becomes "Events 2"). The
    /// standard libraries the interface refers to come into the document when it lacks them.
    /// </summary>
    public static ExternalInterfaceType Attach(InternalElementType owner, string uri, string mimeType, string name)
    {
        var doc = owner.CAEXDocument ?? throw new InvalidOperationException($"'{owner.Name}' is not part of a document.");
        if (doc.FindByPath(ExternalDataReference) == null && doc.CAEXFile.InterfaceClassLib["AutomationMLInterfaceClassLib"] == null)
            Announce(doc, AutomationMLInterfaceClassLib.AddAutomationMLInterfaceClassLib(doc));
        if (doc.CAEXFile.AttributeTypeLib[AttributeTypes] == null)
            Announce(doc, AutomationMLBaseAttributeTypeLib.AddAutomationMLBaseAttributeTypeLib(doc));

        var taken = owner.ExternalInterface.Select(e => e.Name).ToHashSet(StringComparer.Ordinal);
        var unique = name;
        for (var i = 2; taken.Contains(unique); i++) unique = name + " " + i.ToString(CultureInfo.InvariantCulture);
        var attached = owner.ExternalInterface.Append(unique);
        attached.RefBaseClassPath = ExternalDataReference;
        var refUri = attached.Attribute.Append("refURI");
        refUri.AttributeDataType = "xs:anyURI";
        refUri.RefAttributeType = AttributeTypes + "/refURI";
        refUri.Value = uri;
        var mime = attached.Attribute.Append("MIMEType");
        mime.AttributeDataType = "xs:string";
        mime.RefAttributeType = AttributeTypes + "/MIMEType";
        mime.Value = mimeType;
        return attached;
    }

    /// <summary>A library added through XLinq: the editor's library tree hears of it only this way.</summary>
    private static void Announce(CAEXDocument doc, CAEXBasicObject? library)
    {
        if (library != null) EditorServices.Announce(doc, [library]);
    }
}
