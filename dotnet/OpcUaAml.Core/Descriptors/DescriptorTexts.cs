// The rules of the Descriptor check and the templates of what it says, in
// English; the plugin translates a template and puts the names back in.

using System.Globalization;

namespace OpcUaAml.Descriptors;

/// <summary>Something found wrong, as a template of <see cref="DescriptorTexts"/> and the names to put in.</summary>
public sealed record Problem(string Format, IReadOnlyList<string> Args)
{
    public Problem(string format, params object[] args)
        : this(format, (IReadOnlyList<string>)args.Select(a => Convert.ToString(a, CultureInfo.InvariantCulture) ?? "").ToArray()) { }

    public string Message => string.Format(CultureInfo.InvariantCulture, Format, Args.Cast<object?>().ToArray());

    public override string ToString() => Message;
}

/// <summary>The rules of <see cref="DescriptorChecker"/>, with the text a report shows for them.</summary>
public static class DescriptorRules
{
    public const string NotAPackage = "DX001";
    public const string Manifest = "DX002";
    public const string Root = "DX003";
    public const string BrokenRelationship = "DX004";
    public const string Unreachable = "DX005";
    public const string Circular = "DX006";
    public const string ContentTypes = "DX007";
    public const string NoSignature = "DX008";
    public const string SignatureInvalid = "DX009";
    public const string NotSigned = "DX010";
    public const string NoCertificate = "DX011";
    public const string Policy = "DX012";
    public const string Usage = "DX013";
    public const string Validity = "DX014";
    public const string Trust = "DX015";
    public const string NoTime = "DX016";

    public static readonly IReadOnlyDictionary<string, string> Descriptions = new Dictionary<string, string>
    {
        [NotAPackage] = "The file is not an AML container (an OPC package).",
        [Manifest] = "The UAFX manifest is missing, doubled or departs from Annex J.",
        [Root] = "A Root AML file is missing or is no OPC UA FX information model.",
        [BrokenRelationship] = "A relationship points to a file the Descriptor does not hold.",
        [Unreachable] = "A file cannot be reached from a Root AML file through relationships.",
        [Circular] = "Relationships lead in a circle.",
        [ContentTypes] = "[Content_Types].xml lists an extension no file has.",
        [NoSignature] = "The Descriptor is not signed.",
        [SignatureInvalid] = "A signature does not verify, or a file changed after signing.",
        [NotSigned] = "A file is outside the scope of a signature.",
        [NoCertificate] = "A signature carries no X.509 certificate.",
        [Policy] = "An algorithm or key length is outside the UAFX Offline Security Policies.",
        [Usage] = "A signing certificate is not for digital signatures, or is a CA certificate.",
        [Validity] = "A signature was made outside the validity of its certificate.",
        [Trust] = "The chain of a signing certificate is incomplete or not trusted.",
        [NoTime] = "A signature does not say when it was made.",
    };
}

/// <summary>The templates of the Descriptor check's messages, in English.</summary>
public static class DescriptorTexts
{
    public const string NotAPackage = "is not an AML container (an OPC package): {0}";

    public const string NoManifest = "has no UAFX manifest: no file whose root element is DescriptorInfo (OPC 10000-83 7.3.2).";
    public const string ManyManifests = "holds {0} UAFX manifests ({1}); a Descriptor has exactly one.";
    public const string ManifestNotNamed = "is the manifest by its content, but no relationship of /_rels/.rels names it.";
    public const string ManifestOtherType = "is named by the relationship type {0}; this plugin writes {1}, as OPC 10000-83 1.00.03 leaves the type open (7.3.2).";
    public const string ManifestRootElement = "its root element is not DescriptorInfo.";
    public const string ManifestNoNamespace = "DescriptorInfo is in no namespace; Annex J declares {0}.";
    public const string ManifestOtherNamespace = "DescriptorInfo is in the namespace {0}; Annex J declares {1}.";
    public const string ManifestNoIdentifier = "DescriptorIdentifier is missing or empty.";
    public const string ManifestIdentifierNotUri = "DescriptorIdentifier '{0}' is not an absolute URI.";
    public const string ManifestNoVersion = "DescriptorVersion is missing.";
    public const string ManifestNoVersionPart = "DescriptorVersion has no {0}.";
    public const string ManifestVersionPartNotNumber = "{0} of DescriptorVersion is '{1}', not a number from 0 to 65535.";
    public const string ManifestNoFxVersion = "OpcUaFxVersion is missing or empty.";

    public const string NoRoot = "names no Root AML file (relationship RootDocument in /_rels/.rels).";
    public const string RootNotAml = "is a Root AML file, but not an AML document: {0}";
    public const string RootNotFx = "uses no OPC UA FX AML library (SUC_http://opcfoundation.org/UA/FX/…); a Root AML file is an OPC UA FX information model (OPC 10000-83 7.4).";

    public const string BrokenRelationship = "relationship {0} ({1}) points to {2}, which the Descriptor does not hold.";
    public const string Unreachable = "cannot be reached from a Root AML file through relationships (OPC 10000-83 7.5.2).";
    public const string Circular = "relationships lead in a circle: {0}.";
    public const string UnusedExtension = "[Content_Types].xml lists the extension .{0}, which no file has (OPC 10000-83 7.6).";

    public const string NoSignature = "is not signed; a Descriptor has at least one digital signature (OPC 10000-83 7.8.1).";
    public const string SignatureMissing = "the origin lists this signature, but the package does not hold it.";
    public const string SignatureNotXml = "the signature is not XML: {0}";
    public const string NoXmlSignature = "the part holds no XML signature.";
    public const string SignatureUnreadable = "the signature cannot be read: {0}";
    public const string SignatureUncheckable = "the signature cannot be checked: {0}";
    public const string CertificatePartMissing = "the certificate part {0} is missing.";
    public const string CertificatePartUnreadable = "the certificate part {0} cannot be read: {1}";
    public const string ValueInvalid = "the signature value does not verify: SignedInfo or the Object it signs was changed.";
    public const string ReferenceNoPart = "the reference '{0}' names no part.";
    public const string ContentTypeChanged = "{0} was signed as {1} and is now {2}.";
    public const string PartUncheckable = "{0} cannot be checked: {1}";
    public const string PartChanged = "was changed after {0} signed it.";
    public const string PartMissing = "was signed by {0}, but the Descriptor no longer holds it.";
    public const string NotSigned = "is not covered by the signature of {0}.";

    public const string NoCertificate = "the signature carries no X.509 certificate.";
    public const string NoTime = "the signature does not say when it was made (SignatureTime).";
    public const string SignatureAlgorithm = "{0} signs with {1}, which no UAFX Offline Security Policy allows (OPC 10000-84).";
    public const string KeyLength = "the key of {0} has {1} bits; {2} allows {3} to {4}.";
    public const string DigestAlgorithm = "{0} digests with {1}, which no UAFX Offline Security Policy allows (OPC 10000-84).";
    public const string CertificateAlgorithm = "certificate {0} is signed with {1}, which no UAFX Offline Security Policy allows (OPC 10000-84).";
    public const string NotForSigning = "certificate {0} is not for digital signatures (key usage).";
    public const string NoKeyUsage = "certificate {0} states no key usage; OPC 10000-83 Table 3 asks for digitalSignature.";
    public const string IsCa = "certificate {0} is a CA certificate; a Descriptor is signed by an end entity (OPC 10000-83 Table 3).";
    public const string SignedOutsideValidity = "{0} was signed on {1}, outside the validity of certificate {2} ({3} to {4}).";
    public const string NotYetValid = "certificate {0} is valid only from {1}.";
    public const string ChainIncomplete = "the chain of {0} cannot be built: {1}";
    public const string Untrusted = "{0} is not trusted: neither it nor a certificate of its chain is in the list of trusted certificates.";

    public static readonly IReadOnlyList<string> All = typeof(DescriptorTexts)
        .GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
        .Where(f => f.IsLiteral && f.FieldType == typeof(string))
        .Select(f => (string)f.GetRawConstantValue()!)
        .ToList();
}
