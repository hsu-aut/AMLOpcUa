// A certificate to sign Descriptors with when no CA issues one (OPC 10000-83
// 8.3.4: "Ability to create a self-signed certificate"). Self-signed, the
// receiver has to trust it by hand (Annex L.1); the public part is written
// beside it for that.

using System.IO.Packaging;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace OpcUaAml.Descriptors;

public static class DescriptorCertificates
{
    /// <summary>
    /// A self-signed end-entity certificate for digital signatures, valid from a day before
    /// <paramref name="now"/> for <paramref name="years"/> years: RSA 2048 with SHA-256
    /// (Rsa-Pkcs-Sha256), or ECC P-256 with ECDSA SHA-256 (ECC-nistP256).
    /// </summary>
    public static X509Certificate2 CreateSelfSigned(string subject, DateTimeOffset now, int years = 3, bool ecc = false)
    {
        if (!subject.Contains('=')) subject = "CN=" + subject;
        var name = new X500DistinguishedName(subject);
        using var rsa = ecc ? null : RSA.Create(2048);
        using var ec = ecc ? ECDsa.Create(ECCurve.NamedCurves.nistP256) : null;
        var request = ecc
            ? new CertificateRequest(name, ec!, HashAlgorithmName.SHA256)
            : new CertificateRequest(name, rsa!, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.NonRepudiation, true));
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));
        using var made = request.CreateSelfSigned(now.AddDays(-1), now.AddYears(years));
        // Exported and read back: a key that lives only in memory can be saved and used to sign alike.
        return new X509Certificate2(made.Export(X509ContentType.Pfx), (string?)null, X509KeyStorageFlags.Exportable);
    }

    /// <summary>The bytes of the root document of an AML container, or of an AML file.</summary>
    public static byte[] RootDocumentOf(string path)
    {
        if (!Path.GetExtension(path).Equals(".amlx", StringComparison.OrdinalIgnoreCase)) return File.ReadAllBytes(path);
        using var package = Package.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var relation = package.GetRelationshipsByType(DescriptorRelationships.RootDocument).FirstOrDefault()
                       ?? throw new InvalidDataException($"'{path}' names no root document.");
        return PackageSignatures.ReadAll(package.GetPart(PackUriHelper.ResolvePartUri(new Uri("/", UriKind.Relative), relation.TargetUri)));
    }
}
