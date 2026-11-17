// The manifest of a UAFX Descriptor (OPC 10000-83 7.3.2, schema in Annex J):
// one DescriptorInfo element naming the Descriptor, its version and the
// version of OPC UA FX it adheres to.

using System.Globalization;
using System.Xml.Linq;

namespace OpcUaAml.Descriptors;

/// <summary>
/// A Descriptor's version: four numbers of type xs:unsignedShort (Annex J; Table 1 says
/// xs:short, the normative schema unsignedShort).
/// </summary>
public readonly record struct DescriptorVersion(ushort Major, ushort Minor, ushort Build, ushort SubBuild)
{
    /// <summary>"1", "1.2", "1.2.3" or "1.2.3.4"; the numbers left out are 0.</summary>
    public static bool TryParse(string? text, out DescriptorVersion version)
    {
        version = default;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var parts = text.Trim().Split('.');
        if (parts.Length > 4) return false;
        var numbers = new ushort[4];
        for (var i = 0; i < parts.Length; i++)
            if (!ushort.TryParse(parts[i], NumberStyles.None, CultureInfo.InvariantCulture, out numbers[i])) return false;
        version = new DescriptorVersion(numbers[0], numbers[1], numbers[2], numbers[3]);
        return true;
    }

    public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"{Major}.{Minor}.{Build}.{SubBuild}");
}

/// <summary>What the manifest of a Descriptor says about it (OPC 10000-83 7.3.2, Table 1).</summary>
/// <param name="Identifier">A URI, globally unique for the Descriptor.</param>
/// <param name="Version">The version of the Descriptor's content.</param>
/// <param name="OpcUaFxVersion">The version of OPC UA FX, as the FX NodeSet the Descriptor adheres to states it.</param>
public sealed record DescriptorInfo(string Identifier, DescriptorVersion Version, string OpcUaFxVersion)
{
    /// <summary>The target namespace of the schema in Annex J.</summary>
    public const string Namespace = "http://opcfoundation.org/UA/FX/2021/08/DescriptorInfo.xsd";

    private static readonly XNamespace Fx = Namespace;
    private static readonly string[] VersionParts = { "Major", "Minor", "Build", "SubBuild" };

    public XDocument ToXml() => new(
        new XDeclaration("1.0", "utf-8", null),
        new XElement(Fx + "DescriptorInfo",
            new XElement(Fx + "DescriptorIdentifier", Identifier),
            new XElement(Fx + "DescriptorVersion",
                new XElement(Fx + "Major", Version.Major),
                new XElement(Fx + "Minor", Version.Minor),
                new XElement(Fx + "Build", Version.Build),
                new XElement(Fx + "SubBuild", Version.SubBuild)),
            new XElement(Fx + "OpcUaFxVersion", OpcUaFxVersion)));

    /// <summary>Whether a document is a UAFX manifest: its root is DescriptorInfo, in the namespace of Annex J or in none.</summary>
    public static bool IsManifest(XDocument document) =>
        document.Root is { } root && root.Name.LocalName == "DescriptorInfo"
        && (root.Name.Namespace == Fx || root.Name.Namespace == XNamespace.None);

    /// <summary>
    /// Reads a manifest; <paramref name="problems"/> names what departs from Annex J. Null when
    /// it does not say which Descriptor it describes.
    /// </summary>
    public static DescriptorInfo? Read(XDocument document, out IReadOnlyList<string> problems)
    {
        var found = new List<string>();
        problems = found;
        var root = document.Root;
        if (root == null || root.Name.LocalName != "DescriptorInfo")
        {
            found.Add("its root element is not DescriptorInfo.");
            return null;
        }
        var ns = root.Name.Namespace;
        if (ns != Fx)
            found.Add(ns == XNamespace.None
                ? $"DescriptorInfo is in no namespace; Annex J declares {Namespace}."
                : $"DescriptorInfo is in the namespace {ns.NamespaceName}; Annex J declares {Namespace}.");

        var identifier = root.Element(ns + "DescriptorIdentifier")?.Value.Trim();
        if (string.IsNullOrEmpty(identifier)) found.Add("DescriptorIdentifier is missing or empty.");
        else if (!Uri.TryCreate(identifier, UriKind.Absolute, out _)) found.Add($"DescriptorIdentifier '{identifier}' is not an absolute URI.");

        var version = default(DescriptorVersion);
        var versionElement = root.Element(ns + "DescriptorVersion");
        if (versionElement == null) found.Add("DescriptorVersion is missing.");
        else
        {
            var numbers = new ushort[4];
            for (var i = 0; i < 4; i++)
            {
                var part = versionElement.Element(ns + VersionParts[i]);
                if (part == null) found.Add($"DescriptorVersion has no {VersionParts[i]}.");
                else if (!ushort.TryParse(part.Value.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out numbers[i]))
                    found.Add($"{VersionParts[i]} of DescriptorVersion is '{part.Value.Trim()}', not a number from 0 to 65535.");
            }
            version = new DescriptorVersion(numbers[0], numbers[1], numbers[2], numbers[3]);
        }

        var fx = root.Element(ns + "OpcUaFxVersion")?.Value.Trim();
        if (string.IsNullOrEmpty(fx)) found.Add("OpcUaFxVersion is missing or empty.");

        return string.IsNullOrEmpty(identifier) ? null : new DescriptorInfo(identifier, version, fx ?? "");
    }
}
