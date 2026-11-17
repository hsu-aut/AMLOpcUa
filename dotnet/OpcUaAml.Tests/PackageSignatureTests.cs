using System.IO.Packaging;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Xml.Linq;
using OpcUaAml.Descriptors;

namespace OpcUaAml.Tests;

public class PackageSignatureTests
{
    private static readonly XNamespace Ds = "http://www.w3.org/2000/09/xmldsig#";
    private static readonly XNamespace Opc = "http://schemas.openxmlformats.org/package/2006/digital-signature";
    private static readonly DateTimeOffset Now = new(2026, 11, 17, 10, 0, 0, TimeSpan.FromHours(1));

    /// <summary>A small package: a document, an attachment it relates to, the package relationship to the document.</summary>
    internal static MemoryStream NewPackage()
    {
        var stream = new MemoryStream();
        using (var package = Package.Open(stream, FileMode.Create, FileAccess.ReadWrite))
        {
            var doc = package.CreatePart(new Uri("/plant.aml", UriKind.Relative), "application/automationml-aml+xml")!;
            using (var s = doc.GetStream()) new XDocument(new XElement("CAEXFile", new XAttribute("FileName", "plant.aml"))).Save(s);
            package.CreateRelationship(doc.Uri, TargetMode.Internal, "http://schemas.automationml.org/container/relationship/RootDocument");
            var pdf = package.CreatePart(new Uri("/attachments/manual.pdf", UriKind.Relative), "application/pdf")!;
            using (var s = pdf.GetStream()) s.Write("%PDF-1.7 a manual"u8);
            doc.CreateRelationship(pdf.Uri, TargetMode.Internal, "http://schemas.automationml.org/container/relationship/AnyContent");
        }
        stream.Position = 0;
        return stream;
    }

    internal static X509Certificate2 RsaCertificate(int bits = 2048, string name = "CN=Test signer")
    {
        using var rsa = RSA.Create(bits);
        var request = new CertificateRequest(name, rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, critical: true));
        using var cert = request.CreateSelfSigned(Now.AddDays(-1), Now.AddYears(1));
        return new X509Certificate2(cert.Export(X509ContentType.Pfx), (string?)null, X509KeyStorageFlags.Exportable);
    }

    internal static X509Certificate2 EcdsaCertificate(ECCurve curve, HashAlgorithmName hash)
    {
        using var ecdsa = ECDsa.Create(curve);
        var request = new CertificateRequest("CN=Test EC signer", ecdsa, hash);
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, critical: true));
        using var cert = request.CreateSelfSigned(Now.AddDays(-1), Now.AddYears(1));
        return new X509Certificate2(cert.Export(X509ContentType.Pfx), (string?)null, X509KeyStorageFlags.Exportable);
    }

    private static IReadOnlyList<PackageSignature> SignAndRead(MemoryStream stream, X509Certificate2 cert)
    {
        using (var package = Package.Open(stream, FileMode.Open, FileAccess.ReadWrite))
            PackageSignatures.Sign(package, cert, Now);
        stream.Position = 0;
        using var read = Package.Open(stream, FileMode.Open, FileAccess.Read);
        return PackageSignatures.Read(read);
    }

    [Fact]
    public void A_signed_package_verifies()
    {
        using var stream = NewPackage();
        using var cert = RsaCertificate();

        var signature = Assert.Single(SignAndRead(stream, cert));

        Assert.True(signature.ValueValid, string.Join("; ", signature.Problems));
        Assert.Empty(signature.ChangedParts);
        Assert.Empty(signature.MissingParts);
        Assert.Empty(signature.Problems);
        Assert.Equal(cert.Thumbprint, signature.Signer!.Thumbprint);
        Assert.Equal(Now, signature.Time);
        Assert.Equal(PackageSignatures.RsaSha256, signature.SignatureMethod);
    }

    [Fact]
    public void The_signature_covers_every_part_but_the_content_types_and_the_list_of_signatures()
    {
        using var stream = NewPackage();
        using var cert = RsaCertificate();

        var signature = Assert.Single(SignAndRead(stream, cert));

        Assert.Equal(
            new[]
            {
                "/_rels/.rels", "/attachments/manual.pdf", "/package/services/digital-signature/origin.psdsor",
                "/plant.aml", "/_rels/plant.aml.rels",
            }.OrderBy(p => p, StringComparer.Ordinal),
            signature.SignedParts.Select(u => u.OriginalString).OrderBy(p => p, StringComparer.Ordinal));
    }

    [Fact]
    public void The_signature_is_built_as_Annex_I_shows_it()
    {
        using var stream = NewPackage();
        using var cert = RsaCertificate();
        using (var package = Package.Open(stream, FileMode.Open, FileAccess.ReadWrite))
            PackageSignatures.Sign(package, cert, Now);
        stream.Position = 0;

        XDocument xml;
        using (var package = Package.Open(stream, FileMode.Open, FileAccess.Read))
        {
            var origin = Assert.Single(package.GetRelationshipsByType(PackageSignatures.OriginRelationship));
            Assert.Equal("/package/services/digital-signature/origin.psdsor", origin.TargetUri.OriginalString);
            var originPart = package.GetPart(origin.TargetUri);
            var toSignature = Assert.Single(originPart.GetRelationshipsByType(PackageSignatures.SignatureRelationship));
            var part = package.GetPart(PackUriHelper.ResolvePartUri(originPart.Uri, toSignature.TargetUri));
            Assert.StartsWith("/package/services/digital-signature/xml-signature/", part.Uri.OriginalString);
            Assert.EndsWith(".psdsxs", part.Uri.OriginalString);
            Assert.Equal(PackageSignatures.SignatureContentType, part.ContentType);
            using var s = part.GetStream(FileMode.Open, FileAccess.Read);
            xml = XDocument.Load(s);
        }

        var signature = xml.Root!;
        Assert.Equal(Ds + "Signature", signature.Name);
        var signedInfo = signature.Element(Ds + "SignedInfo")!;
        Assert.Equal("http://www.w3.org/2006/12/xml-c14n11", signedInfo.Element(Ds + "CanonicalizationMethod")!.Attribute("Algorithm")!.Value);
        Assert.Equal("http://www.w3.org/2001/04/xmldsig-more#rsa-sha256", signedInfo.Element(Ds + "SignatureMethod")!.Attribute("Algorithm")!.Value);
        var reference = Assert.Single(signedInfo.Elements(Ds + "Reference"));
        Assert.Equal("#idPackageObject", reference.Attribute("URI")!.Value);
        Assert.Equal("http://www.w3.org/2000/09/xmldsig#Object", reference.Attribute("Type")!.Value);
        Assert.NotNull(signature.Element(Ds + "KeyInfo")!.Element(Ds + "X509Data")!.Element(Ds + "X509Certificate"));

        var obj = signature.Element(Ds + "Object")!;
        Assert.Equal("idPackageObject", obj.Attribute("Id")!.Value);
        var references = obj.Element(Ds + "Manifest")!.Elements(Ds + "Reference").ToList();
        var rels = references.Single(r => r.Attribute("URI")!.Value.StartsWith("/_rels/.rels?", StringComparison.Ordinal));
        Assert.Equal("/_rels/.rels?ContentType=application/vnd.openxmlformats-package.relationships+xml", rels.Attribute("URI")!.Value);
        Assert.Equal("http://www.w3.org/2006/12/xml-c14n11",
            rels.Element(Ds + "Transforms")!.Element(Ds + "Transform")!.Attribute("Algorithm")!.Value);
        var pdf = references.Single(r => r.Attribute("URI")!.Value.StartsWith("/attachments/manual.pdf?", StringComparison.Ordinal));
        Assert.Null(pdf.Element(Ds + "Transforms"));
        Assert.Equal("http://www.w3.org/2001/04/xmlenc#sha256", pdf.Element(Ds + "DigestMethod")!.Attribute("Algorithm")!.Value);

        var time = obj.Element(Ds + "SignatureProperties")!.Element(Ds + "SignatureProperty")!.Element(Opc + "SignatureTime")!;
        Assert.Equal("YYYY-MM-DDThh:mm:ss.sTZD", time.Element(Opc + "Format")!.Value);
        Assert.Equal("2026-11-17T10:00:00.0+01:00", time.Element(Opc + "Value")!.Value);
    }

    [Fact]
    public void A_part_changed_after_signing_is_named()
    {
        using var stream = NewPackage();
        using var cert = RsaCertificate();
        using (var package = Package.Open(stream, FileMode.Open, FileAccess.ReadWrite))
            PackageSignatures.Sign(package, cert, Now);
        stream.Position = 0;
        using (var package = Package.Open(stream, FileMode.Open, FileAccess.ReadWrite))
        {
            using var s = package.GetPart(new Uri("/attachments/manual.pdf", UriKind.Relative)).GetStream(FileMode.Create, FileAccess.Write);
            s.Write("%PDF-1.7 another manual"u8);
        }
        stream.Position = 0;

        using var read = Package.Open(stream, FileMode.Open, FileAccess.Read);
        var signature = Assert.Single(PackageSignatures.Read(read));

        Assert.True(signature.ValueValid);
        Assert.Equal("/attachments/manual.pdf", Assert.Single(signature.ChangedParts).OriginalString);
    }

    [Fact]
    public void A_part_removed_after_signing_is_named()
    {
        using var stream = NewPackage();
        using var cert = RsaCertificate();
        using (var package = Package.Open(stream, FileMode.Open, FileAccess.ReadWrite))
            PackageSignatures.Sign(package, cert, Now);
        stream.Position = 0;
        using (var package = Package.Open(stream, FileMode.Open, FileAccess.ReadWrite))
            package.DeletePart(new Uri("/attachments/manual.pdf", UriKind.Relative));
        stream.Position = 0;

        using var read = Package.Open(stream, FileMode.Open, FileAccess.Read);
        var signature = Assert.Single(PackageSignatures.Read(read));

        Assert.Equal("/attachments/manual.pdf", Assert.Single(signature.MissingParts).OriginalString);
    }

    [Fact]
    public void A_signature_whose_value_was_changed_does_not_verify()
    {
        using var stream = NewPackage();
        using var cert = RsaCertificate();
        using (var package = Package.Open(stream, FileMode.Open, FileAccess.ReadWrite))
        {
            PackageSignatures.Sign(package, cert, Now);
            var part = package.GetParts().Single(p => p.Uri.OriginalString.EndsWith(".psdsxs", StringComparison.Ordinal));
            XDocument xml;
            using (var s = part.GetStream(FileMode.Open, FileAccess.Read)) xml = XDocument.Load(s, LoadOptions.PreserveWhitespace);
            // Another signing time: the Object changes, its digest in SignedInfo no longer fits.
            xml.Descendants(Opc + "Value").Single().Value = "2020-01-01T00:00:00.0+00:00";
            using (var s = part.GetStream(FileMode.Create, FileAccess.Write)) xml.Save(s, SaveOptions.DisableFormatting);
        }
        stream.Position = 0;

        using var read = Package.Open(stream, FileMode.Open, FileAccess.Read);
        var signature = Assert.Single(PackageSignatures.Read(read));

        Assert.False(signature.ValueValid);
    }

    [Theory]
    [InlineData("nistP256", "http://www.w3.org/2001/04/xmldsig-more#ecdsa-sha256")]
    [InlineData("nistP384", "http://www.w3.org/2001/04/xmldsig-more#ecdsa-sha384")]
    [InlineData("nistP521", "http://www.w3.org/2001/04/xmldsig-more#ecdsa-sha512")]
    public void An_ECC_key_signs_with_the_hash_of_its_curve(string curve, string method)
    {
        using var stream = NewPackage();
        var named = ECCurve.CreateFromFriendlyName(curve);
        var hash = curve switch { "nistP256" => HashAlgorithmName.SHA256, "nistP384" => HashAlgorithmName.SHA384, _ => HashAlgorithmName.SHA512 };
        using var cert = EcdsaCertificate(named, hash);

        var signature = Assert.Single(SignAndRead(stream, cert));

        Assert.True(signature.ValueValid, string.Join("; ", signature.Problems));
        Assert.Equal(method, signature.SignatureMethod);
        Assert.Empty(signature.ChangedParts);
    }

    [Fact]
    public void A_second_signature_covers_the_first_and_leaves_it_valid()
    {
        using var stream = NewPackage();
        using var first = RsaCertificate(name: "CN=Vendor");
        using var second = RsaCertificate(3072, "CN=Controls engineer");
        using (var package = Package.Open(stream, FileMode.Open, FileAccess.ReadWrite))
            PackageSignatures.Sign(package, first, Now);
        stream.Position = 0;

        var signatures = SignAndRead(stream, second);

        Assert.Equal(2, signatures.Count);
        Assert.All(signatures, s =>
        {
            Assert.True(s.ValueValid, string.Join("; ", s.Problems));
            Assert.Empty(s.ChangedParts);
        });
        var vendor = signatures.Single(s => s.Signer!.Subject == "CN=Vendor");
        var engineer = signatures.Single(s => s.Signer!.Subject == "CN=Controls engineer");
        Assert.Contains(vendor.Part, engineer.SignedParts);
    }

    [Fact]
    public void A_package_without_signatures_has_none()
    {
        using var stream = NewPackage();
        using var read = Package.Open(stream, FileMode.Open, FileAccess.Read);

        Assert.Empty(PackageSignatures.Read(read));
    }
}
