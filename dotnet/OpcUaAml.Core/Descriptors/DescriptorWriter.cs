// Writes a UAFX Descriptor (OPC 10000-83 clause 7): an AML container whose
// Root AML file is the document, with the manifest, the attachments and the
// Descriptors embedded in it, signed at the end as 8.2 asks of every export.
//
//   /station.aml                    Root AML file (RootDocument)
//   /manifest.xml                   UAFX manifest (Manifest, from /_rels/.rels)
//   /attachments/manual.pdf         attachment (AnyContent, from the root)
//   /descriptors/vendor.amlx        embedded Descriptor (EmbeddedDescriptor, from the root)
//   /package/services/digital-signature/...   origin and signature
//
// Part 83 prescribes no folders (7.3.1); the relationships say what a file is.

using System.IO.Packaging;
using System.Security.Cryptography.X509Certificates;
using System.Xml;

namespace OpcUaAml.Descriptors;

/// <summary>The relationship types of a Descriptor (OPC 10000-83 7.3 to 7.5).</summary>
public static class DescriptorRelationships
{
    public const string RootDocument = "http://schemas.automationml.org/container/relationship/RootDocument";
    public const string Library = "http://schemas.automationml.org/container/relationship/Library";
    public const string AnyContent = "http://schemas.automationml.org/container/relationship/AnyContent";
    public const string UafxInformationModel = "http://schemas.opcfoundation.org/container/relationship/UafxInformationModel";
    public const string EmbeddedDescriptor = "http://schemas.opcfoundation.org/container/relationship/EmbeddedDescriptor";

    /// <summary>
    /// The relationship naming the UAFX manifest. OPC 10000-83 1.00.03 leaves the type empty
    /// (7.3.2, also Example 1); Figure 17 calls the relationship "Manifest" and names every other
    /// one by the last segment of its type, and the other UAFX types are under
    /// http://schemas.opcfoundation.org/container/relationship/. The check finds a manifest by its
    /// content and only notes another type.
    /// </summary>
    public const string Manifest = "http://schemas.opcfoundation.org/container/relationship/Manifest";

    public const string CoreProperties = "http://schemas.openxmlformats.org/package/2006/relationships/metadata/core-properties";
    public const string Thumbnail = "http://schemas.openxmlformats.org/package/2006/relationships/metadata/thumbnail";
}

/// <summary>A file going into a Descriptor: its name and its content.</summary>
public sealed record DescriptorFile(string Name, byte[] Content);

/// <summary>What a Descriptor is made of.</summary>
public sealed class DescriptorContent
{
    public required DescriptorInfo Info { get; init; }

    /// <summary>The Root AML file: an OPC UA FX information model.</summary>
    public required DescriptorFile Root { get; init; }

    /// <summary>Manuals, pictures, anything else; a file that is a Descriptor itself is embedded as one.</summary>
    public IReadOnlyList<DescriptorFile> Attachments { get; init; } = Array.Empty<DescriptorFile>();
}

public static class DescriptorWriter
{
    /// <summary>OPC 10000-83 Table 2.</summary>
    public const string AmlContentType = "application/automationml-aml+xml";
    public const string AmlxContentType = "application/automationml-amlx+zip";
    public const string ManifestPart = "/manifest.xml";

    private static readonly Dictionary<string, string> ContentTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["aml"] = AmlContentType,
        ["amlx"] = AmlxContentType,
        ["xml"] = "text/xml",
        ["xsd"] = "text/xml",
        ["pdf"] = "application/pdf",
        ["png"] = "image/png",
        ["jpg"] = "image/jpeg",
        ["jpeg"] = "image/jpeg",
        ["gif"] = "image/gif",
        ["svg"] = "image/svg+xml",
        ["txt"] = "text/plain",
        ["csv"] = "text/csv",
        ["htm"] = "text/html",
        ["html"] = "text/html",
        ["json"] = "application/json",
        ["zip"] = "application/zip",
        ["dae"] = "model/vnd.collada+xml",
        ["stp"] = "model/step",
        ["step"] = "model/step",
    };

    /// <summary>The content type a file of this name goes in as.</summary>
    public static string ContentTypeOf(string name) =>
        ContentTypes.TryGetValue(Path.GetExtension(name).TrimStart('.'), out var type) ? type : "application/octet-stream";

    /// <summary>
    /// Writes <paramref name="content"/> as a Descriptor to <paramref name="path"/>, signed by
    /// <paramref name="signer"/> as of <paramref name="time"/>; <paramref name="chain"/> goes into
    /// the signature with the signer's certificate.
    /// </summary>
    public static void Write(string path, DescriptorContent content, X509Certificate2 signer, DateTimeOffset time,
        IEnumerable<X509Certificate2>? chain = null)
    {
        var folder = Path.GetDirectoryName(Path.GetFullPath(path))!;
        var temp = Path.Combine(folder, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            using (var package = Package.Open(temp, FileMode.Create, FileAccess.ReadWrite))
            {
                var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var rootName = content.Root.Name.EndsWith(".aml", StringComparison.OrdinalIgnoreCase) ? content.Root.Name : content.Root.Name + ".aml";
                var root = Add(package, PartUri("", rootName, used), AmlContentType, content.Root.Content);
                package.CreateRelationship(root.Uri, TargetMode.Internal, DescriptorRelationships.RootDocument);

                using (var manifestXml = new MemoryStream())
                {
                    using (var writer = XmlWriter.Create(manifestXml, new XmlWriterSettings { Encoding = new System.Text.UTF8Encoding(false), Indent = true }))
                        content.Info.ToXml().Save(writer);
                    var manifest = Add(package, PartUri("", "manifest.xml", used), ContentTypeOf("manifest.xml"), manifestXml.ToArray());
                    package.CreateRelationship(manifest.Uri, TargetMode.Internal, DescriptorRelationships.Manifest);
                }

                foreach (var file in content.Attachments)
                {
                    var embedded = IsDescriptor(file.Content);
                    var part = Add(package, PartUri(embedded ? "descriptors" : "attachments", file.Name, used),
                        embedded ? AmlxContentType : ContentTypeOf(file.Name), file.Content);
                    root.CreateRelationship(part.Uri, TargetMode.Internal,
                        embedded ? DescriptorRelationships.EmbeddedDescriptor : DescriptorRelationships.AnyContent);
                }

                PackageSignatures.Sign(package, signer, time, chain);
            }
            File.Move(temp, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
    }

    /// <summary>
    /// The version of OPC UA FX a document's FX libraries were made from: ModelVersion of the
    /// OpcUaLibInfo of http://opcfoundation.org/UA/FX/AC/ (Annex K), else of another FX model;
    /// null when the document has no FX library.
    /// </summary>
    public static string? FxVersionOf(byte[] aml)
    {
        string? any = null;
        foreach (var (uri, version) in FxModels.LibInfos(aml))
        {
            if (uri.TrimEnd('/') == "http://opcfoundation.org/UA/FX/AC") return version;
            if (uri.StartsWith(FxModels.Prefix, StringComparison.Ordinal)) any ??= version;
        }
        return any;
    }

    /// <summary>Whether bytes are a Descriptor: a package holding a UAFX manifest.</summary>
    internal static bool IsDescriptor(byte[] content)
    {
        if (content.Length < 4 || content[0] != 'P' || content[1] != 'K') return false;
        try
        {
            using var package = Package.Open(new MemoryStream(content, writable: false), FileMode.Open, FileAccess.Read);
            return package.GetParts().Any(p => FxModels.IsManifestPart(p));
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or XmlException or ArgumentException or NotSupportedException
                                       or InvalidOperationException or FormatException)
        {
            return false;
        }
    }

    private static PackagePart Add(Package package, Uri uri, string contentType, byte[] content)
    {
        var part = package.CreatePart(uri, contentType, CompressionOption.Normal)!;
        using var stream = part.GetStream(FileMode.Create, FileAccess.Write);
        stream.Write(content);
        return part;
    }

    /// <summary>A part name in a folder, numbered when the name is taken (OPC part names ignore case).</summary>
    private static Uri PartUri(string folder, string name, HashSet<string> used)
    {
        var file = Path.GetFileName(name);
        if (string.IsNullOrWhiteSpace(file)) file = "file";
        var stem = Path.GetFileNameWithoutExtension(file);
        var extension = Path.GetExtension(file);
        for (var n = 1; ; n++)
        {
            var candidate = n == 1 ? file : $"{stem}_{n}{extension}";
            var path = (folder.Length == 0 ? "/" : $"/{folder}/") + Uri.EscapeDataString(candidate);
            var uri = PackUriHelper.CreatePartUri(new Uri(path, UriKind.Relative));
            if (used.Add(uri.OriginalString)) return uri;
        }
    }
}

/// <summary>What the check and the writer read from AML and manifest files without loading them whole.</summary>
internal static class FxModels
{
    public const string Prefix = "http://opcfoundation.org/UA/FX/";
    private const string LibInfoNamespace = "http://opcfoundation.org/UA/FX/2021/08/OpcUaLibInfo.xsd";

    private static XmlReader Reader(Stream stream) =>
        XmlReader.Create(stream, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, IgnoreWhitespace = true });

    /// <summary>The namespace URI and model version of every OpcUaLibInfo in an AML document (Annex K).</summary>
    public static IEnumerable<(string Uri, string Version)> LibInfos(byte[] aml)
    {
        var found = new List<(string, string)>();
        try
        {
            using var reader = Reader(new MemoryStream(aml, writable: false));
            while (reader.Read())
            {
                if (reader.NodeType != XmlNodeType.Element || reader.LocalName != "OpcUaLibInfo" || reader.NamespaceURI != LibInfoNamespace) continue;
                using var sub = reader.ReadSubtree();
                string? uri = null, version = null;
                sub.Read();
                while (!sub.EOF)
                {
                    // Reading an element's content moves on to the next node already.
                    if (sub.NodeType == XmlNodeType.Element && sub.LocalName == "OpcUaNamespaceUri") uri = sub.ReadElementContentAsString().Trim();
                    else if (sub.NodeType == XmlNodeType.Element && sub.LocalName == "ModelVersion") version = sub.ReadElementContentAsString().Trim();
                    else sub.Read();
                }
                if (uri != null && version != null) found.Add((uri, version));
            }
        }
        catch (XmlException) { }
        return found;
    }

    /// <summary>The root element of an XML document, or null when it is not XML.</summary>
    public static (string LocalName, string Namespace)? RootOf(Stream stream)
    {
        try
        {
            using var reader = Reader(stream);
            return reader.MoveToContent() == XmlNodeType.Element ? (reader.LocalName, reader.NamespaceURI) : null;
        }
        catch (XmlException)
        {
            return null;
        }
    }

    /// <summary>Whether an AML document refers to an OPC UA FX AML library (XXX_http://opcfoundation.org/UA/FX/…).</summary>
    public static bool UsesFx(Stream stream)
    {
        const string marker = "_" + Prefix;
        try
        {
            using var reader = Reader(stream);
            while (reader.Read())
            {
                if (reader.NodeType != XmlNodeType.Element || !reader.HasAttributes) continue;
                while (reader.MoveToNextAttribute())
                    if (reader.Value.Contains(marker, StringComparison.Ordinal)) return true;
            }
        }
        catch (XmlException) { }
        return false;
    }

    /// <summary>Whether a part could be XML: its content type says so, or its name ends in .xml.</summary>
    public static bool MayBeXml(PackagePart part) =>
        part.ContentType.Contains("xml", StringComparison.OrdinalIgnoreCase)
        || part.Uri.OriginalString.EndsWith(".xml", StringComparison.OrdinalIgnoreCase);

    /// <summary>Whether a part is a UAFX manifest by its content: its root is DescriptorInfo.</summary>
    public static bool IsManifestPart(PackagePart part)
    {
        if (PackUriHelper.IsRelationshipPartUri(part.Uri) || !MayBeXml(part)
            || string.Equals(part.ContentType, PackageSignatures.SignatureContentType, StringComparison.OrdinalIgnoreCase)) return false;
        using var stream = part.GetStream(FileMode.Open, FileAccess.Read);
        return RootOf(stream) is { LocalName: "DescriptorInfo" } root
               && (root.Namespace == DescriptorInfo.Namespace || root.Namespace.Length == 0);
    }
}
