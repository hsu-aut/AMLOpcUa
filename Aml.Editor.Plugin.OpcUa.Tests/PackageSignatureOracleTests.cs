using System.IO;
using System.IO.Packaging;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Xml.Linq;
using OpcUaAml.Descriptors;

namespace PluginTests;

/// <summary>
/// The signatures of UAFX Descriptors against WPF's PackageDigitalSignatureManager, an
/// implementation of ISO/IEC 29500-2 clause 13 written without this code.
/// </summary>
public class PackageSignatureOracleTests
{
    private const string RootDocument = "http://schemas.automationml.org/container/relationship/RootDocument";
    private const string AnyContent = "http://schemas.automationml.org/container/relationship/AnyContent";
    private static readonly DateTimeOffset Now = DateTimeOffset.Now;

    private static MemoryStream NewPackage()
    {
        var stream = new MemoryStream();
        using (var package = Package.Open(stream, FileMode.Create, FileAccess.ReadWrite))
        {
            var doc = package.CreatePart(new Uri("/plant.aml", UriKind.Relative), "application/automationml-aml+xml")!;
            using (var s = doc.GetStream()) new XDocument(new XElement("CAEXFile", new XAttribute("FileName", "plant.aml"))).Save(s);
            package.CreateRelationship(doc.Uri, TargetMode.Internal, RootDocument);
            var pdf = package.CreatePart(new Uri("/attachments/manual.pdf", UriKind.Relative), "application/pdf")!;
            using (var s = pdf.GetStream()) s.Write("%PDF-1.7 a manual"u8);
            doc.CreateRelationship(pdf.Uri, TargetMode.Internal, AnyContent);
        }
        stream.Position = 0;
        return stream;
    }

    private static X509Certificate2 Certificate()
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=Oracle signer", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, critical: true));
        using var cert = request.CreateSelfSigned(Now.AddDays(-1), Now.AddYears(1));
        return new X509Certificate2(cert.Export(X509ContentType.Pfx), (string?)null, X509KeyStorageFlags.Exportable);
    }

    [Fact]
    public void A_package_the_reference_implementation_signs_verifies_here()
    {
        using var stream = NewPackage();
        using var cert = Certificate();
        using (var package = Package.Open(stream, FileMode.Open, FileAccess.ReadWrite))
        {
            var manager = new PackageDigitalSignatureManager(package)
            {
                HashAlgorithm = PackageSignatures.Sha256,
                CertificateOption = CertificateEmbeddingOption.InSignaturePart,
            };
            var parts = package.GetParts().Where(p => !PackUriHelper.IsRelationshipPartUri(p.Uri)).Select(p => p.Uri).ToList();
            // Relationships are signed through the Relationships Transform, selected by type.
            var selectors = new List<PackageRelationshipSelector>
            {
                new(new Uri("/", UriKind.Relative), PackageRelationshipSelectorType.Type, RootDocument),
                new(new Uri("/plant.aml", UriKind.Relative), PackageRelationshipSelectorType.Type, AnyContent),
            };
            manager.Sign(parts, cert, selectors);
        }
        stream.Position = 0;

        using var read = Package.Open(stream, FileMode.Open, FileAccess.Read);
        var signature = Assert.Single(PackageSignatures.Read(read));

        Assert.True(signature.ValueValid, string.Join("; ", signature.Problems));
        Assert.Empty(signature.ChangedParts);
        Assert.Empty(signature.MissingParts);
        Assert.Contains(signature.SignedParts, u => u.OriginalString == "/_rels/.rels");
        Assert.Contains(signature.SignedParts, u => u.OriginalString == "/_rels/plant.aml.rels");
        Assert.Equal(cert.Thumbprint, signature.Signer!.Thumbprint);
    }

    [Fact]
    public void A_relationship_changed_after_the_reference_implementation_signed_is_named()
    {
        using var stream = NewPackage();
        using var cert = Certificate();
        using (var package = Package.Open(stream, FileMode.Open, FileAccess.ReadWrite))
        {
            var manager = new PackageDigitalSignatureManager(package) { HashAlgorithm = PackageSignatures.Sha256 };
            var parts = package.GetParts().Where(p => !PackUriHelper.IsRelationshipPartUri(p.Uri)).Select(p => p.Uri).ToList();
            var selectors = new List<PackageRelationshipSelector>
            {
                new(new Uri("/plant.aml", UriKind.Relative), PackageRelationshipSelectorType.Type, AnyContent),
            };
            manager.Sign(parts, cert, selectors);
        }
        stream.Position = 0;
        using (var package = Package.Open(stream, FileMode.Open, FileAccess.ReadWrite))
        {
            // Another attachment of the same type: the signed selection of relationships grows.
            var doc = package.GetPart(new Uri("/plant.aml", UriKind.Relative));
            var png = package.CreatePart(new Uri("/attachments/photo.png", UriKind.Relative), "image/png")!;
            using (var s = png.GetStream()) s.Write(new byte[] { 0x89, 0x50, 0x4E, 0x47 });
            doc.CreateRelationship(png.Uri, TargetMode.Internal, AnyContent);
        }
        stream.Position = 0;

        using var read = Package.Open(stream, FileMode.Open, FileAccess.Read);
        var signature = Assert.Single(PackageSignatures.Read(read));

        Assert.Equal("/_rels/plant.aml.rels", Assert.Single(signature.ChangedParts).OriginalString);
    }

    [Fact]
    public void A_signature_made_here_verifies_with_the_reference_implementation()
    {
        using var stream = NewPackage();
        using var cert = Certificate();
        using (var package = Package.Open(stream, FileMode.Open, FileAccess.ReadWrite))
            // Canonical XML 1.0: the reference implementation knows no other.
            PackageSignatures.Sign(package, cert, Now, canonicalization: PackageSignatures.C14N);
        stream.Position = 0;

        using var read = Package.Open(stream, FileMode.Open, FileAccess.Read);
        var manager = new PackageDigitalSignatureManager(read);

        Assert.True(manager.IsSigned);
        Assert.Equal(VerifyResult.Success, manager.VerifySignatures(exitOnFailure: false));
        Assert.Equal(cert.Thumbprint, ((X509Certificate2)Assert.Single(manager.Signatures).Signer).Thumbprint);
    }
}
