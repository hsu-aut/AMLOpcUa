// Digital signatures of OPC packages (ISO/IEC 29500-2, clause 13) the way
// OPC 10000-83 7.8 and Annex I sign a UAFX Descriptor: one XML signature per
// part /package/services/digital-signature/xml-signature/*.psdsxs, listed by
// the relationships of the origin part. SignedInfo signs one Object; the
// Manifest in it holds a digest of every part, and SignatureProperties the
// time of signing. Relationship parts and earlier signatures are digested
// after canonicalization, every other part as its bytes.
//
// The scope is every part but [Content_Types].xml and the relationships of
// the origin part, which list the signatures (7.8.1): a later signature does
// not break an earlier one, and covers it.
//
// Canonical XML 1.1, which Annex I names, differs from 1.0 only where xml:id
// and xml:base are inherited into a part of a document. A whole part has no
// ancestors, and the SignedInfo and Object of these signatures carry no xml:
// attributes, so both give the same octets; .NET implements 1.0, which serves
// for both names.

using System.ComponentModel;
using System.Globalization;
using System.IO.Packaging;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Security.Cryptography.Xml;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace OpcUaAml.Descriptors;

/// <summary>One signature of a package and what checking it found.</summary>
public sealed class PackageSignature
{
    public PackageSignature(Uri part) => Part = part;

    /// <summary>The part holding the signature (*.psdsxs).</summary>
    public Uri Part { get; }

    /// <summary>The certificate whose key the signature value verifies with; else the one it names first.</summary>
    public X509Certificate2? Signer { get; init; }

    /// <summary>Every certificate the signature carries: the signer's and those of its chain.</summary>
    public IReadOnlyList<X509Certificate2> Certificates { get; init; } = Array.Empty<X509Certificate2>();

    /// <summary>When it was signed, as the signature says (SignatureTime).</summary>
    public DateTimeOffset? Time { get; init; }

    public string SignatureMethod { get; init; } = "";

    /// <summary>The digest methods of the signature's references.</summary>
    public IReadOnlyList<string> DigestMethods { get; init; } = Array.Empty<string>();

    /// <summary>Whether SignedInfo verifies with <see cref="Signer"/>'s key, the Object with the part digests included.</summary>
    public bool ValueValid { get; init; }

    /// <summary>The parts the signature covers.</summary>
    public IReadOnlyList<Uri> SignedParts { get; init; } = Array.Empty<Uri>();

    /// <summary>Signed parts whose content no longer has the digest signed.</summary>
    public IReadOnlyList<Uri> ChangedParts { get; init; } = Array.Empty<Uri>();

    /// <summary>Signed parts the package no longer holds.</summary>
    public IReadOnlyList<Uri> MissingParts { get; init; } = Array.Empty<Uri>();

    /// <summary>What else stands in the way of trusting it, in English.</summary>
    public IReadOnlyList<string> Problems { get; init; } = Array.Empty<string>();
}

public static class PackageSignatures
{
    public const string OriginRelationship = "http://schemas.openxmlformats.org/package/2006/relationships/digital-signature/origin";
    public const string SignatureRelationship = "http://schemas.openxmlformats.org/package/2006/relationships/digital-signature/signature";
    public const string CertificateRelationship = "http://schemas.openxmlformats.org/package/2006/relationships/digital-signature/certificate";
    public const string OriginContentType = "application/vnd.openxmlformats-package.digital-signature-origin";
    public const string SignatureContentType = "application/vnd.openxmlformats-package.digital-signature-xmlsignature+xml";
    public const string RelationshipsContentType = "application/vnd.openxmlformats-package.relationships+xml";

    public const string C14N = "http://www.w3.org/TR/2001/REC-xml-c14n-20010315";
    public const string C14NWithComments = "http://www.w3.org/TR/2001/REC-xml-c14n-20010315#WithComments";
    public const string C14N11 = "http://www.w3.org/2006/12/xml-c14n11";
    public const string C14N11WithComments = "http://www.w3.org/2006/12/xml-c14n11#WithComments";
    public const string RelationshipTransform = "http://schemas.openxmlformats.org/package/2006/RelationshipTransform";

    public const string Sha1 = "http://www.w3.org/2000/09/xmldsig#sha1";
    public const string Sha256 = "http://www.w3.org/2001/04/xmlenc#sha256";
    public const string Sha384 = "http://www.w3.org/2001/04/xmldsig-more#sha384";
    public const string Sha512 = "http://www.w3.org/2001/04/xmlenc#sha512";

    public const string RsaSha1 = "http://www.w3.org/2000/09/xmldsig#rsa-sha1";
    public const string RsaSha256 = "http://www.w3.org/2001/04/xmldsig-more#rsa-sha256";
    public const string RsaSha384 = "http://www.w3.org/2001/04/xmldsig-more#rsa-sha384";
    public const string RsaSha512 = "http://www.w3.org/2001/04/xmldsig-more#rsa-sha512";
    public const string RsaPssSha256 = "http://www.w3.org/2007/05/xmldsig-more#sha256-rsa-MGF1";
    public const string RsaPssSha384 = "http://www.w3.org/2007/05/xmldsig-more#sha384-rsa-MGF1";
    public const string RsaPssSha512 = "http://www.w3.org/2007/05/xmldsig-more#sha512-rsa-MGF1";
    public const string EcdsaSha256 = "http://www.w3.org/2001/04/xmldsig-more#ecdsa-sha256";
    public const string EcdsaSha384 = "http://www.w3.org/2001/04/xmldsig-more#ecdsa-sha384";
    public const string EcdsaSha512 = "http://www.w3.org/2001/04/xmldsig-more#ecdsa-sha512";

    private const string Ds = SignedXml.XmlDsigNamespaceUrl;
    private const string OpcDs = "http://schemas.openxmlformats.org/package/2006/digital-signature";
    private const string RelationshipsNamespace = "http://schemas.openxmlformats.org/package/2006/relationships";
    private const string ObjectId = "idPackageObject";
    private const string SignatureId = "SignatureIdValue";
    private const string TimeFormat = "YYYY-MM-DDThh:mm:ss.sTZD";

    private static readonly Uri OriginUri = new("/package/services/digital-signature/origin.psdsor", UriKind.Relative);

    private static readonly Lazy<bool> Registered = new(() =>
    {
        CryptoConfig.AddAlgorithm(typeof(XmlDsigC14N11Transform), C14N11);
        CryptoConfig.AddAlgorithm(typeof(XmlDsigC14N11WithCommentsTransform), C14N11WithComments);
        CryptoConfig.AddAlgorithm(typeof(EcdsaSha256SignatureDescription), EcdsaSha256);
        CryptoConfig.AddAlgorithm(typeof(EcdsaSha384SignatureDescription), EcdsaSha384);
        CryptoConfig.AddAlgorithm(typeof(EcdsaSha512SignatureDescription), EcdsaSha512);
        CryptoConfig.AddAlgorithm(typeof(RsaPssSha256SignatureDescription), RsaPssSha256);
        CryptoConfig.AddAlgorithm(typeof(RsaPssSha384SignatureDescription), RsaPssSha384);
        CryptoConfig.AddAlgorithm(typeof(RsaPssSha512SignatureDescription), RsaPssSha512);
        return true;
    });

    private static void EnsureAlgorithms() => _ = Registered.Value;

    /// <summary>
    /// Signs every part of <paramref name="package"/> (but [Content_Types].xml and the list of
    /// signatures) with <paramref name="certificate"/>'s private key, as of <paramref name="time"/>.
    /// The certificate and <paramref name="chain"/> go into the signature (KeyInfo/X509Data).
    /// An RSA key signs with RSA PKCS#1 v1.5 and SHA-256 (SHA-512 above 4096 bits), an ECC key
    /// with ECDSA and the hash of its curve: the UAFX Offline Security Policies of OPC 10000-84.
    /// </summary>
    /// <returns>The part the signature is in.</returns>
    public static Uri Sign(Package package, X509Certificate2 certificate, DateTimeOffset time,
        IEnumerable<X509Certificate2>? chain = null, string canonicalization = C14N11)
    {
        EnsureAlgorithms();
        if (!certificate.HasPrivateKey)
            throw new ArgumentException($"The certificate {certificate.Subject} has no private key to sign with.", nameof(certificate));
        AsymmetricAlgorithm key = (AsymmetricAlgorithm?)certificate.GetRSAPrivateKey() ?? certificate.GetECDsaPrivateKey()
                                  ?? throw new ArgumentException($"The key of {certificate.Subject} is neither RSA nor ECC.", nameof(certificate));
        var (method, digest) = MethodsFor(key);

        var origin = OriginPart(package);
        if (origin == null)
        {
            origin = package.CreatePart(OriginUri, OriginContentType, CompressionOption.Normal)!;
            package.CreateRelationship(origin.Uri, TargetMode.Internal, OriginRelationship);
        }
        package.Flush();

        var originRelationships = PackUriHelper.GetRelationshipPartUri(origin.Uri);
        var work = new XmlDocument { PreserveWhitespace = true };
        var holder = work.CreateElement("Holder", Ds);
        var manifest = work.CreateElement("Manifest", Ds);
        holder.AppendChild(manifest);
        foreach (var part in package.GetParts().Where(p => p.Uri != originRelationships).OrderBy(p => p.Uri.OriginalString, StringComparer.Ordinal))
        {
            var canonical = IsXmlCommonService(part);
            var bytes = ReadAll(part);
            if (canonical) bytes = Canonicalize(bytes, comments: false);
            var reference = work.CreateElement("Reference", Ds);
            reference.SetAttribute("URI", $"{part.Uri.OriginalString}?ContentType={part.ContentType}");
            if (canonical)
            {
                var transforms = work.CreateElement("Transforms", Ds);
                var transform = work.CreateElement("Transform", Ds);
                transform.SetAttribute("Algorithm", canonicalization);
                transforms.AppendChild(transform);
                reference.AppendChild(transforms);
            }
            var digestMethod = work.CreateElement("DigestMethod", Ds);
            digestMethod.SetAttribute("Algorithm", digest);
            reference.AppendChild(digestMethod);
            var digestValue = work.CreateElement("DigestValue", Ds);
            digestValue.InnerText = Convert.ToBase64String(Hash(digest, bytes));
            reference.AppendChild(digestValue);
            manifest.AppendChild(reference);
        }

        var properties = work.CreateElement("SignatureProperties", Ds);
        var property = work.CreateElement("SignatureProperty", Ds);
        property.SetAttribute("Id", "idSignatureTime");
        property.SetAttribute("Target", "#" + SignatureId);
        var signatureTime = work.CreateElement("SignatureTime", OpcDs);
        var format = work.CreateElement("Format", OpcDs);
        format.InnerText = TimeFormat;
        var value = work.CreateElement("Value", OpcDs);
        value.InnerText = time.ToString("yyyy-MM-dd'T'HH:mm:ss.fzzz", CultureInfo.InvariantCulture);
        signatureTime.AppendChild(format);
        signatureTime.AppendChild(value);
        property.AppendChild(signatureTime);
        properties.AppendChild(property);
        holder.AppendChild(properties);

        var signed = new SignedXml { SigningKey = key };
        signed.Signature.Id = SignatureId;
        signed.SignedInfo!.CanonicalizationMethod = canonicalization;
        signed.SignedInfo.SignatureMethod = method;
        signed.AddObject(new DataObject { Id = ObjectId, Data = holder.ChildNodes });
        var objectReference = new Reference("#" + ObjectId) { Type = "http://www.w3.org/2000/09/xmldsig#Object", DigestMethod = digest };
        signed.AddReference(objectReference);
        var x509 = new KeyInfoX509Data(certificate);
        foreach (var c in chain ?? Enumerable.Empty<X509Certificate2>())
            if (c.Thumbprint != certificate.Thumbprint) x509.AddCertificate(c);
        signed.KeyInfo = new KeyInfo();
        signed.KeyInfo.AddClause(x509);
        signed.ComputeSignature();

        var output = new XmlDocument { PreserveWhitespace = true };
        output.AppendChild(output.CreateXmlDeclaration("1.0", "utf-8", "yes"));
        output.AppendChild(output.ImportNode(signed.GetXml(), deep: true));
        var signatureUri = PackUriHelper.CreatePartUri(new Uri(
            $"/package/services/digital-signature/xml-signature/{Guid.NewGuid():N}.psdsxs", UriKind.Relative));
        var signaturePart = package.CreatePart(signatureUri, SignatureContentType, CompressionOption.Normal)!;
        using (var stream = signaturePart.GetStream(FileMode.Create, FileAccess.Write))
        using (var writer = XmlWriter.Create(stream, new XmlWriterSettings { Encoding = new UTF8Encoding(false) }))
            output.Save(writer);
        origin.CreateRelationship(signatureUri, TargetMode.Internal, SignatureRelationship);
        package.Flush();
        return signatureUri;
    }

    /// <summary>The signature method and digest method for a key (OPC 10000-84, UAFX Offline Security Policies).</summary>
    internal static (string Method, string Digest) MethodsFor(AsymmetricAlgorithm key) => key switch
    {
        RSA rsa when rsa.KeySize > 4096 => (RsaSha512, Sha512),
        RSA => (RsaSha256, Sha256),
        ECDsa ec when ec.KeySize > 384 => (EcdsaSha512, Sha512),
        ECDsa ec when ec.KeySize > 256 => (EcdsaSha384, Sha384),
        ECDsa => (EcdsaSha256, Sha256),
        _ => throw new ArgumentException($"A {key.GetType().Name} key cannot sign a Descriptor.", nameof(key)),
    };

    /// <summary>The package's origin part, which the signatures hang off; null when it has none.</summary>
    public static PackagePart? OriginPart(Package package)
    {
        var relation = package.GetRelationshipsByType(OriginRelationship).FirstOrDefault(r => r.TargetMode == TargetMode.Internal);
        if (relation == null) return null;
        var uri = PackUriHelper.ResolvePartUri(new Uri("/", UriKind.Relative), relation.TargetUri);
        return package.PartExists(uri) ? package.GetPart(uri) : null;
    }

    /// <summary>The signatures of a package, each checked against the package as it is.</summary>
    public static IReadOnlyList<PackageSignature> Read(Package package)
    {
        EnsureAlgorithms();
        var origin = OriginPart(package);
        if (origin == null) return Array.Empty<PackageSignature>();
        var signatures = new List<PackageSignature>();
        foreach (var relation in origin.GetRelationshipsByType(SignatureRelationship))
        {
            if (relation.TargetMode != TargetMode.Internal) continue;
            var uri = PackUriHelper.ResolvePartUri(origin.Uri, relation.TargetUri);
            signatures.Add(package.PartExists(uri)
                ? Check(package, package.GetPart(uri))
                : new PackageSignature(uri) { Problems = new[] { "the origin lists this signature, but the package does not hold it." } });
        }
        return signatures;
    }

    private static PackageSignature Check(Package package, PackagePart part)
    {
        var problems = new List<string>();
        var document = new XmlDocument { PreserveWhitespace = true, XmlResolver = null };
        try
        {
            using var stream = part.GetStream(FileMode.Open, FileAccess.Read);
            using var reader = XmlReader.Create(stream, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
            document.Load(reader);
        }
        catch (XmlException ex)
        {
            return new PackageSignature(part.Uri) { Problems = new[] { $"the signature is not XML: {ex.Message}" } };
        }
        if (document.DocumentElement is not { LocalName: "Signature", NamespaceURI: Ds } root)
            return new PackageSignature(part.Uri) { Problems = new[] { "the part holds no XML signature." } };

        var signed = new SignedXml(document);
        signed.SafeCanonicalizationMethods.Add(C14N11);
        signed.SafeCanonicalizationMethods.Add(C14N11WithComments);
        try
        {
            signed.LoadXml(root);
        }
        catch (CryptographicException ex)
        {
            return new PackageSignature(part.Uri) { Problems = new[] { $"the signature cannot be read: {ex.Message}" } };
        }

        var certificates = new List<X509Certificate2>();
        foreach (var clause in signed.KeyInfo?.OfType<KeyInfoX509Data>() ?? Enumerable.Empty<KeyInfoX509Data>())
            foreach (var c in clause.Certificates?.OfType<X509Certificate>() ?? Enumerable.Empty<X509Certificate>())
                certificates.Add(new X509Certificate2(c));
        foreach (var relation in part.GetRelationshipsByType(CertificateRelationship))
        {
            var uri = PackUriHelper.ResolvePartUri(part.Uri, relation.TargetUri);
            if (!package.PartExists(uri)) { problems.Add($"the certificate part {uri} is missing."); continue; }
            try { certificates.Add(new X509Certificate2(ReadAll(package.GetPart(uri)))); }
            catch (CryptographicException ex) { problems.Add($"the certificate part {uri} cannot be read: {ex.Message}"); }
        }
        if (certificates.Count == 0) problems.Add("the signature carries no X.509 certificate.");

        X509Certificate2? signer = null;
        var valid = false;
        foreach (var certificate in certificates)
        {
            try
            {
                if (signed.CheckSignature(certificate, verifySignatureOnly: true)) { signer = certificate; valid = true; break; }
            }
            catch (CryptographicException ex)
            {
                problems.Add($"the signature cannot be checked: {ex.Message}");
                break;
            }
        }
        signer ??= certificates.FirstOrDefault(c => !certificates.Any(o => o != c && o.Issuer == c.Subject && o.Subject != c.Subject))
                   ?? certificates.FirstOrDefault();
        if (!valid && certificates.Count > 0 && problems.Count == 0)
            problems.Add("the signature value does not verify: SignedInfo or the Object it signs was changed.");

        // Only the Objects SignedInfo signs count: a Manifest outside them proves nothing.
        var signedObjects = signed.SignedInfo!.References.OfType<Reference>()
            .Select(r => r.Uri ?? "").Where(u => u.StartsWith('#')).Select(u => u[1..]).ToHashSet(StringComparer.Ordinal);
        var digestMethods = signed.SignedInfo.References.OfType<Reference>().Select(r => r.DigestMethod ?? "").ToList();
        var signedParts = new List<Uri>();
        var changed = new List<Uri>();
        var missing = new List<Uri>();
        DateTimeOffset? time = null;
        var ns = new XmlNamespaceManager(document.NameTable);
        ns.AddNamespace("ds", Ds);
        ns.AddNamespace("opc", OpcDs);
        foreach (XmlElement obj in root.SelectNodes("ds:Object", ns)!)
        {
            if (!signedObjects.Contains(obj.GetAttribute("Id"))) continue;
            foreach (XmlElement reference in obj.SelectNodes("ds:Manifest/ds:Reference", ns)!)
            {
                var (uri, contentType) = SplitPartReference(reference.GetAttribute("URI"));
                if (uri == null) { problems.Add($"the reference '{reference.GetAttribute("URI")}' names no part."); continue; }
                signedParts.Add(uri);
                var digestMethod = (reference.SelectSingleNode("ds:DigestMethod", ns) as XmlElement)?.GetAttribute("Algorithm") ?? "";
                digestMethods.Add(digestMethod);
                if (!package.PartExists(uri)) { missing.Add(uri); continue; }
                var target = package.GetPart(uri);
                if (contentType != null && !string.Equals(contentType, target.ContentType, StringComparison.OrdinalIgnoreCase))
                {
                    problems.Add($"{uri} was signed as {contentType} and is now {target.ContentType}.");
                    changed.Add(uri);
                    continue;
                }
                try
                {
                    var bytes = ReadAll(target);
                    foreach (XmlElement transform in reference.SelectNodes("ds:Transforms/ds:Transform", ns)!)
                        bytes = Apply(transform, bytes);
                    var expected = Convert.FromBase64String((reference.SelectSingleNode("ds:DigestValue", ns)?.InnerText ?? "").Trim());
                    if (!CryptographicOperations.FixedTimeEquals(Hash(digestMethod, bytes), expected)) changed.Add(uri);
                }
                catch (Exception ex) when (ex is NotSupportedException or XmlException or FormatException)
                {
                    problems.Add($"{uri} cannot be checked: {ex.Message}");
                    changed.Add(uri);
                }
            }
            var value = obj.SelectSingleNode("ds:SignatureProperties/ds:SignatureProperty/opc:SignatureTime/opc:Value", ns)?.InnerText.Trim();
            if (value != null && DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed))
                time = parsed;
        }
        if (time == null) problems.Add("the signature does not say when it was made (SignatureTime).");

        return new PackageSignature(part.Uri)
        {
            Signer = signer,
            Certificates = certificates,
            Time = time,
            SignatureMethod = signed.SignedInfo.SignatureMethod ?? "",
            DigestMethods = digestMethods.Distinct(StringComparer.Ordinal).ToList(),
            ValueValid = valid,
            SignedParts = signedParts,
            ChangedParts = changed,
            MissingParts = missing,
            Problems = problems,
        };
    }

    /// <summary>"/a/b.xml?ContentType=text/xml" into the part's URI and the content type it was signed as.</summary>
    private static (Uri? Part, string? ContentType) SplitPartReference(string reference)
    {
        if (string.IsNullOrEmpty(reference) || reference[0] != '/') return (null, null);
        var query = reference.IndexOf('?');
        var path = query < 0 ? reference : reference[..query];
        string? contentType = null;
        if (query >= 0)
        {
            var q = reference[(query + 1)..];
            const string key = "ContentType=";
            if (q.StartsWith(key, StringComparison.OrdinalIgnoreCase)) contentType = Uri.UnescapeDataString(q[key.Length..]).Trim();
        }
        try
        {
            return (PackUriHelper.CreatePartUri(new Uri(path, UriKind.Relative)), contentType);
        }
        catch (ArgumentException)
        {
            return (null, null);
        }
    }

    private static byte[] Apply(XmlElement transform, byte[] input) => transform.GetAttribute("Algorithm") switch
    {
        C14N or C14N11 => Canonicalize(input, comments: false),
        C14NWithComments or C14N11WithComments => Canonicalize(input, comments: true),
        RelationshipTransform => SelectRelationships(input, transform),
        var other => throw new NotSupportedException($"the transform {other} is not supported."),
    };

    /// <summary>
    /// The Relationships Transform (ISO/IEC 29500-2, 13.2.4.24): the relationships the transform
    /// selects by Id or by type, sorted by Id, without anything else, TargetMode always written.
    /// A canonicalization follows it in the signature.
    /// </summary>
    private static byte[] SelectRelationships(byte[] input, XmlElement transform)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var types = new HashSet<string>(StringComparer.Ordinal);
        foreach (var child in transform.ChildNodes.OfType<XmlElement>().Where(e => e.NamespaceURI == OpcDs))
            if (child.LocalName == "RelationshipReference") ids.Add(child.GetAttribute("SourceId"));
            else if (child.LocalName == "RelationshipsGroupReference") types.Add(child.GetAttribute("SourceType"));
        XNamespace r = RelationshipsNamespace;
        var document = SafeXml.Load(new MemoryStream(input));
        var kept = (document.Root?.Elements(r + "Relationship") ?? Enumerable.Empty<XElement>())
            .Where(e => ids.Contains((string?)e.Attribute("Id") ?? "") || types.Contains((string?)e.Attribute("Type") ?? ""))
            .OrderBy(e => (string?)e.Attribute("Id") ?? "", StringComparer.Ordinal)
            .Select(e => new XElement(r + "Relationship",
                e.Attributes().Where(a => !a.IsNamespaceDeclaration && a.Name.Namespace == XNamespace.None && a.Name.LocalName != "TargetMode")
                    .Select(a => new XAttribute(a.Name, a.Value)),
                new XAttribute("TargetMode", (string?)e.Attribute("TargetMode") ?? "Internal")));
        var result = new XElement(r + "Relationships", new XAttribute("xmlns", RelationshipsNamespace), kept);
        return Encoding.UTF8.GetBytes(result.ToString(SaveOptions.DisableFormatting));
    }

    internal static byte[] Canonicalize(byte[] xml, bool comments)
    {
        var document = new XmlDocument { PreserveWhitespace = true, XmlResolver = null };
        using (var reader = XmlReader.Create(new MemoryStream(xml), new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null }))
            document.Load(reader);
        var transform = new XmlDsigC14NTransform(comments);
        transform.LoadInput(document);
        using var output = (Stream)transform.GetOutput(typeof(Stream));
        using var copy = new MemoryStream();
        output.CopyTo(copy);
        return copy.ToArray();
    }

    internal static byte[] Hash(string digestMethod, byte[] data) => digestMethod switch
    {
        Sha256 => SHA256.HashData(data),
        Sha384 => SHA384.HashData(data),
        Sha512 => SHA512.HashData(data),
#pragma warning disable CA5350 // read only, to name an old signature; the policy check refuses it
        Sha1 => SHA1.HashData(data),
#pragma warning restore CA5350
        _ => throw new NotSupportedException($"the digest method {digestMethod} is not supported."),
    };

    /// <summary>Relationship parts and signatures: XML a package library may write anew, digested canonical.</summary>
    private static bool IsXmlCommonService(PackagePart part) =>
        PackUriHelper.IsRelationshipPartUri(part.Uri)
        || string.Equals(part.ContentType, SignatureContentType, StringComparison.OrdinalIgnoreCase);

    internal static byte[] ReadAll(PackagePart part)
    {
        using var stream = part.GetStream(FileMode.Open, FileAccess.Read);
        using var copy = new MemoryStream();
        stream.CopyTo(copy);
        return copy.ToArray();
    }
}

// The algorithms .NET's SignedXml does not bring, registered by URI with CryptoConfig,
// which wants their types public.

/// <summary>Canonical XML 1.1 (http://www.w3.org/2006/12/xml-c14n11); see the head of PackageSignatures.cs.</summary>
[EditorBrowsable(EditorBrowsableState.Never)]
public sealed class XmlDsigC14N11Transform : XmlDsigC14NTransform
{
    public XmlDsigC14N11Transform() : base(includeComments: false) => Algorithm = PackageSignatures.C14N11;
}

[EditorBrowsable(EditorBrowsableState.Never)]
public sealed class XmlDsigC14N11WithCommentsTransform : XmlDsigC14NTransform
{
    public XmlDsigC14N11WithCommentsTransform() : base(includeComments: true) => Algorithm = PackageSignatures.C14N11WithComments;
}

[EditorBrowsable(EditorBrowsableState.Never)]
public abstract class AsymmetricSignatureDescription : SignatureDescription
{
    private readonly HashAlgorithmName _hash;

    protected AsymmetricSignatureDescription(Type keyType, HashAlgorithmName hash)
    {
        _hash = hash;
        KeyAlgorithm = keyType.AssemblyQualifiedName;
        DigestAlgorithm = hash.Name;
    }

    public override HashAlgorithm CreateDigest() => _hash.Name switch
    {
        "SHA256" => SHA256.Create(),
        "SHA384" => SHA384.Create(),
        _ => SHA512.Create(),
    };

    public override AsymmetricSignatureFormatter CreateFormatter(AsymmetricAlgorithm key) => new Formatter(key, _hash);

    public override AsymmetricSignatureDeformatter CreateDeformatter(AsymmetricAlgorithm key) => new Deformatter(key, _hash);

    private static byte[] Sign(AsymmetricAlgorithm key, byte[] hash, HashAlgorithmName name) => key switch
    {
        ECDsa ec => ec.SignHash(hash),
        RSA rsa => rsa.SignHash(hash, name, RSASignaturePadding.Pss),
        _ => throw new CryptographicException($"A {key.GetType().Name} key cannot make this signature."),
    };

    private static bool Verify(AsymmetricAlgorithm key, byte[] hash, byte[] signature, HashAlgorithmName name) => key switch
    {
        ECDsa ec => ec.VerifyHash(hash, signature),
        RSA rsa => rsa.VerifyHash(hash, signature, name, RSASignaturePadding.Pss),
        _ => false,
    };

    private sealed class Formatter : AsymmetricSignatureFormatter
    {
        private AsymmetricAlgorithm _key;
        private readonly HashAlgorithmName _hash;
        public Formatter(AsymmetricAlgorithm key, HashAlgorithmName hash) { _key = key; _hash = hash; }
        public override void SetKey(AsymmetricAlgorithm key) => _key = key;
        public override void SetHashAlgorithm(string strName) { }
        public override byte[] CreateSignature(byte[] rgbHash) => Sign(_key, rgbHash, _hash);
    }

    private sealed class Deformatter : AsymmetricSignatureDeformatter
    {
        private AsymmetricAlgorithm _key;
        private readonly HashAlgorithmName _hash;
        public Deformatter(AsymmetricAlgorithm key, HashAlgorithmName hash) { _key = key; _hash = hash; }
        public override void SetKey(AsymmetricAlgorithm key) => _key = key;
        public override void SetHashAlgorithm(string strName) { }
        public override bool VerifySignature(byte[] rgbHash, byte[] rgbSignature) => Verify(_key, rgbHash, rgbSignature, _hash);
    }
}

[EditorBrowsable(EditorBrowsableState.Never)]
public sealed class EcdsaSha256SignatureDescription : AsymmetricSignatureDescription
{
    public EcdsaSha256SignatureDescription() : base(typeof(ECDsa), HashAlgorithmName.SHA256) { }
}

[EditorBrowsable(EditorBrowsableState.Never)]
public sealed class EcdsaSha384SignatureDescription : AsymmetricSignatureDescription
{
    public EcdsaSha384SignatureDescription() : base(typeof(ECDsa), HashAlgorithmName.SHA384) { }
}

[EditorBrowsable(EditorBrowsableState.Never)]
public sealed class EcdsaSha512SignatureDescription : AsymmetricSignatureDescription
{
    public EcdsaSha512SignatureDescription() : base(typeof(ECDsa), HashAlgorithmName.SHA512) { }
}

[EditorBrowsable(EditorBrowsableState.Never)]
public sealed class RsaPssSha256SignatureDescription : AsymmetricSignatureDescription
{
    public RsaPssSha256SignatureDescription() : base(typeof(RSA), HashAlgorithmName.SHA256) { }
}

[EditorBrowsable(EditorBrowsableState.Never)]
public sealed class RsaPssSha384SignatureDescription : AsymmetricSignatureDescription
{
    public RsaPssSha384SignatureDescription() : base(typeof(RSA), HashAlgorithmName.SHA384) { }
}

[EditorBrowsable(EditorBrowsableState.Never)]
public sealed class RsaPssSha512SignatureDescription : AsymmetricSignatureDescription
{
    public RsaPssSha512SignatureDescription() : base(typeof(RSA), HashAlgorithmName.SHA512) { }
}
