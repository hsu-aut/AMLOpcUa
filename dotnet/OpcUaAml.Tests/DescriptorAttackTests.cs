using System.IO.Compression;
using System.IO.Packaging;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Security.Cryptography.Xml;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using OpcUaAml.Checks;
using OpcUaAml.Descriptors;

namespace OpcUaAml.Tests;

/// <summary>
/// What someone who sends a Descriptor to an engineer might do to it, from the
/// security review of 2026-11-18: each of these was reported as "valid and
/// trusted", or passed the command line, before. A Descriptor that was changed,
/// signed by someone else or signed weakly must not be trusted.
/// </summary>
public sealed class DescriptorAttackTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 11, 17, 10, 0, 0, TimeSpan.FromHours(1));
    private const string Ds = "http://www.w3.org/2000/09/xmldsig#";
    private const string OpcDs = "http://schemas.openxmlformats.org/package/2006/digital-signature";
    private const string CertificateContentType = "application/vnd.openxmlformats-package.digital-signature-certificate";

    private readonly string _folder = Directory.CreateTempSubdirectory("opcuaaml-attack-").FullName;
    private readonly X509Certificate2 _vendor = Rsa("CN=HSU Controls");

    public void Dispose()
    {
        _vendor.Dispose();
        try { Directory.Delete(_folder, recursive: true); } catch (IOException) { }
    }

    private DescriptorTrust VendorTrusted => new(new[] { Public(_vendor) });

    private static X509Certificate2 Public(X509Certificate2 c) => new(c.Export(X509ContentType.Cert));

    private static X509Certificate2 Rsa(string name, int bits = 2048)
    {
        using var rsa = RSA.Create(bits);
        var request = new CertificateRequest(name, rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, critical: true));
        using var cert = request.CreateSelfSigned(Now.AddDays(-1), Now.AddYears(1));
        return new X509Certificate2(cert.Export(X509ContentType.Pfx), (string?)null, X509KeyStorageFlags.Exportable | X509KeyStorageFlags.EphemeralKeySet);
    }

    private static byte[] Document(string extra = "") => extra.Length == 0
        ? DescriptorTests.FxDocument()
        : Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(DescriptorTests.FxDocument())
            .Replace("</InstanceHierarchy>", $"<InternalElement Name=\"{extra}\" ID=\"5b1c6a3e-1d0e-4c55-9f37-7c7e0b3f0a09\" /></InstanceHierarchy>"));

    private string Write(string name, X509Certificate2 signer, IEnumerable<X509Certificate2>? chain = null)
    {
        var path = Path.Combine(_folder, name);
        DescriptorWriter.Write(path, new DescriptorContent
        {
            Info = new DescriptorInfo("urn:hsu:mps500:station1", new DescriptorVersion(1, 0, 0, 0), "1.00.03"),
            Root = new DescriptorFile("station.aml", Document()),
            Attachments = new[] { new DescriptorFile("manual.pdf", "%PDF-1.7 a manual"u8.ToArray()) },
        }, signer, Now, chain);
        return path;
    }

    /// <summary>A Descriptor as the writer makes it, with its signature and origin taken out again.</summary>
    private string WriteUnsigned(string name)
    {
        using var throwaway = Rsa("CN=throwaway");
        var path = Write(name, throwaway);
        using var package = Package.Open(path, FileMode.Open, FileAccess.ReadWrite);
        var origin = PackageSignatures.OriginPart(package)!;
        foreach (var r in origin.GetRelationships().ToList()) package.DeletePart(PackUriHelper.ResolvePartUri(origin.Uri, r.TargetUri));
        foreach (var r in package.GetRelationshipsByType(PackageSignatures.OriginRelationship).ToList()) package.DeleteRelationship(r.Id);
        package.DeletePart(origin.Uri);
        return path;
    }

    private static DescriptorReport Check(string path, DescriptorTrust trust) => DescriptorChecker.Check(path, trust, Now.AddHours(1));

    private static string Describe(DescriptorReport report) => string.Join(Environment.NewLine, report.Findings);

    private static void Zip(string path, Action<ZipArchive> change)
    {
        using var zip = ZipFile.Open(path, ZipArchiveMode.Update);
        change(zip);
    }

    private static byte[] Read(ZipArchiveEntry e)
    {
        using var s = e.Open();
        var m = new MemoryStream();
        s.CopyTo(m);
        return m.ToArray();
    }

    private static void Put(ZipArchive zip, string name, byte[] data)
    {
        zip.GetEntry(name)?.Delete();
        AddRaw(zip, name, data);
    }

    private static void AddRaw(ZipArchive zip, string name, byte[] data)
    {
        using var s = zip.CreateEntry(name).Open();
        s.Write(data);
    }

    private static void Override(ZipArchive zip, string partName, string contentType)
    {
        XNamespace n = "http://schemas.openxmlformats.org/package/2006/content-types";
        var types = XDocument.Load(new MemoryStream(Read(zip.GetEntry("[Content_Types].xml")!)));
        types.Root!.Add(new XElement(n + "Override", new XAttribute("PartName", partName), new XAttribute("ContentType", contentType)));
        Put(zip, "[Content_Types].xml", Encoding.UTF8.GetBytes(types.ToString(SaveOptions.DisableFormatting)));
    }

    /// <summary>Not trusted, and the check says why as an error.</summary>
    private static void Refused(DescriptorReport report)
    {
        Assert.True(report.HasErrors, "no error: " + Describe(report));
        Assert.All(report.Signatures, s => Assert.False(s.Trusted, $"{s.SignerName} trusted: " + Describe(report)));
    }

    [Fact]
    public void The_untouched_Descriptor_is_trusted()
    {
        var report = Check(Write("plain.amlx", _vendor), VendorTrusted);

        Assert.False(report.HasErrors, Describe(report));
        Assert.True(Assert.Single(report.Signatures).Trusted);
    }

    // Entries the package library passes over: a program unpacking the container took them,
    // ZipFile.ExtractToDirectory wrote station.aml over the signed one.
    [Theory]
    [InlineData("x/../station.aml")]
    [InlineData("../evil.txt")]
    [InlineData(@"attachments\manual.pdf")]
    [InlineData("/abs.aml")]
    [InlineData("[trash]/0000.dat")]
    [InlineData("station.aml/")]
    [InlineData("st%61tion.aml")]
    [InlineData("ſtation.aml")]
    public void An_entry_beside_the_parts_is_an_error(string entry)
    {
        var path = Write("stray.amlx", _vendor);
        Zip(path, z => AddRaw(z, entry, Document("Backdoor")));

        var report = Check(path, VendorTrusted);

        Refused(report);
        Assert.Contains(report.Findings, f => f.Rule == DescriptorRules.StrayEntry && f.Args.Contains(entry));
    }

    // [Content_Types].xml is not signed: a part given the content type of a signature or a
    // certificate was taken as one, outside every signature's scope.
    [Theory]
    [InlineData(CertificateContentType)]
    [InlineData(PackageSignatures.SignatureContentType)]
    public void A_part_dressed_as_a_signature_or_certificate_is_still_to_be_signed(string contentType)
    {
        var path = Write("dressed.amlx", _vendor);
        Zip(path, z =>
        {
            Put(z, "payload/evil.aml", Encoding.UTF8.GetBytes("<CAEXFile>evil</CAEXFile>"));
            Override(z, "/payload/evil.aml", contentType);
        });

        var report = Check(path, VendorTrusted);

        Refused(report);
        Assert.Contains(report.Findings, f => f.Rule == DescriptorRules.NotSigned && f.ElementPath == "/payload/evil.aml");
    }

    [Fact]
    public void A_file_added_after_signing_makes_the_signature_not_hold()
    {
        var path = Write("added.amlx", _vendor);
        Zip(path, z => { Put(z, "extra.txt", "added later"u8.ToArray()); Override(z, "/extra.txt", "text/plain"); });

        var report = Check(path, VendorTrusted);

        Refused(report);
        Assert.False(Assert.Single(report.Signatures).Valid);
    }

    // Content changed, the vendor's signature taken out, signed anew by someone who calls himself the vendor.
    [Fact]
    public void A_Descriptor_signed_anew_by_someone_else_fails()
    {
        var path = Write("resigned.amlx", _vendor);
        using var attacker = Rsa("CN=HSU Controls");
        using (var package = Package.Open(path, FileMode.Open, FileAccess.ReadWrite))
        {
            var root = package.GetPart(new Uri("/station.aml", UriKind.Relative));
            using (var s = root.GetStream(FileMode.Create, FileAccess.Write)) s.Write(Document("Backdoor"));
            var origin = PackageSignatures.OriginPart(package)!;
            foreach (var r in origin.GetRelationships().ToList()) package.DeletePart(PackUriHelper.ResolvePartUri(origin.Uri, r.TargetUri));
            foreach (var r in package.GetRelationshipsByType(PackageSignatures.OriginRelationship).ToList()) package.DeleteRelationship(r.Id);
            package.DeletePart(origin.Uri);
            PackageSignatures.Sign(package, attacker, Now);
        }

        var report = Check(path, VendorTrusted);

        Refused(report);
        Assert.Contains(report.Findings, f => f.Format == DescriptorTexts.Untrusted && f.Severity == Severity.Error);
    }

    [Fact]
    public void A_signature_over_a_changed_file_is_not_trusted()
    {
        var path = Write("changed.amlx", _vendor);
        Zip(path, z => Put(z, "station.aml", Document("Backdoor")));

        var report = Check(path, VendorTrusted);

        Refused(report);
        var verdict = Assert.Single(report.Signatures);
        Assert.True(verdict.SignerTrusted);
        Assert.False(verdict.Valid);
    }

    [Fact]
    public void A_trusted_signer_with_a_short_key_is_not_trusted()
    {
        using var weak = Rsa("CN=Weak Vendor", 1024);

        var report = Check(Write("weak.amlx", weak), new DescriptorTrust(new[] { Public(weak) }));

        Refused(report);
        Assert.Contains(report.Findings, f => f.Rule == DescriptorRules.Policy);
    }

    [Fact]
    public void A_trusted_CA_with_a_short_key_is_named()
    {
        using var caKey = RSA.Create(1024);
        var caRequest = new CertificateRequest("CN=Weak Root CA", caKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        caRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        caRequest.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
        using var ca = caRequest.CreateSelfSigned(Now.AddDays(-10), Now.AddYears(5));
        using var leafKey = RSA.Create(2048);
        var leafRequest = new CertificateRequest("CN=Engineering tool", leafKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        leafRequest.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
        leafRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        using var issued = leafRequest.Create(ca, Now.AddDays(-1), Now.AddYears(1), new byte[] { 1, 2, 3, 4 });
        using var withKey = issued.CopyWithPrivateKey(leafKey);
        using var leaf = new X509Certificate2(withKey.Export(X509ContentType.Pfx), (string?)null, X509KeyStorageFlags.Exportable);
        using var caPublic = Public(ca);

        var report = Check(Write("weakca.amlx", leaf, new[] { caPublic }), new DescriptorTrust(new[] { caPublic }));

        Refused(report);
        Assert.Contains(report.Findings, f => f.Format == DescriptorTexts.CertificateKeyLength);
    }

    [Fact]
    public void A_key_on_a_curve_the_policies_do_not_name_is_not_trusted()
    {
        using var ec = ECDsa.Create(ECCurve.NamedCurves.brainpoolP256r1);
        var request = new CertificateRequest("CN=Brainpool Vendor", ec, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
        using var made = request.CreateSelfSigned(Now.AddDays(-1), Now.AddYears(1));
        using var brainpool = new X509Certificate2(made.Export(X509ContentType.Pfx), (string?)null, X509KeyStorageFlags.Exportable);

        var report = Check(Write("brainpool.amlx", brainpool), new DescriptorTrust(new[] { Public(brainpool) }));

        Refused(report);
        Assert.Contains(report.Findings, f => f.Format is DescriptorTexts.Curve or DescriptorTexts.CertificateCurve);
    }

    // A signature whose Manifest names no part skipped the scope check.
    [Fact]
    public void A_signature_that_covers_no_part_is_not_trusted()
    {
        using var signer = Rsa("CN=Key used elsewhere");
        var path = WriteUnsigned("nothing.amlx");
        using (var package = Package.Open(path, FileMode.Open, FileAccess.ReadWrite))
            CustomSign(package, signer, emptyManifest: true);

        var report = Check(path, new DescriptorTrust(new[] { Public(signer) }));

        Refused(report);
        Assert.Contains(report.Findings, f => f.Format == DescriptorTexts.SignsNothing);
    }

    // /_rels/.rels signed through the Relationships Transform, selecting RootDocument only:
    // the part counted as signed, and relationships to anywhere could be added.
    [Fact]
    public void Relationships_a_transform_leaves_out_are_not_signed()
    {
        var path = WriteUnsigned("transform.amlx");
        using (var package = Package.Open(path, FileMode.Open, FileAccess.ReadWrite))
            CustomSign(package, _vendor, relsSourceType: DescriptorRelationships.RootDocument);
        Zip(path, z =>
        {
            XNamespace r = "http://schemas.openxmlformats.org/package/2006/relationships";
            var rels = XDocument.Load(new MemoryStream(Read(z.GetEntry("_rels/.rels")!)));
            rels.Root!.Add(new XElement(r + "Relationship", new XAttribute("Id", "Revil1"), new XAttribute("Type", DescriptorRelationships.Library),
                new XAttribute("Target", "https://evil.example/lib.aml"), new XAttribute("TargetMode", "External")));
            Put(z, "_rels/.rels", Encoding.UTF8.GetBytes(rels.ToString(SaveOptions.DisableFormatting)));
        });

        var report = Check(path, VendorTrusted);

        Refused(report);
        Assert.Contains(report.Findings, f => f.Format == DescriptorTexts.RelationshipNotSigned && f.Args.Contains("Revil1"));
        Assert.Contains(report.Findings, f => f.Format == DescriptorTexts.RelationshipNotSigned && f.Args.Contains(DescriptorRelationships.Manifest));
    }

    // A certificate nobody can use, put first into the unsigned KeyInfo, ended the whole check.
    [Fact]
    public void An_unusable_certificate_before_the_signer_does_not_end_the_check()
    {
        var path = Write("bogus.amlx", _vendor);
        using var issuerKey = RSA.Create(2048);
        var unknownKey = new PublicKey(new Oid("1.3.6.1.4.1.99999.1"), new AsnEncodedData(new byte[] { 0x05, 0x00 }), new AsnEncodedData(new byte[] { 0x03, 0x02, 0x00, 0x00 }));
        var request = new CertificateRequest(new X500DistinguishedName("CN=Bogus key"), unknownKey, HashAlgorithmName.SHA256);
        using var bogus = request.Create(new X500DistinguishedName("CN=Bogus issuer"), X509SignatureGenerator.CreateForRSA(issuerKey, RSASignaturePadding.Pkcs1),
            Now.AddDays(-1), Now.AddYears(1), new byte[] { 9 });
        Zip(path, z =>
        {
            var entry = z.Entries.First(e => e.FullName.EndsWith(".psdsxs", StringComparison.Ordinal));
            var name = entry.FullName;
            var doc = new XmlDocument { PreserveWhitespace = true };
            doc.Load(new MemoryStream(Read(entry)));
            var ns = new XmlNamespaceManager(doc.NameTable);
            ns.AddNamespace("ds", Ds);
            var x509 = (XmlElement)doc.SelectSingleNode("//ds:X509Data", ns)!;
            var c = doc.CreateElement("X509Certificate", Ds);
            c.InnerText = Convert.ToBase64String(bogus.RawData);
            x509.PrependChild(c);
            var m = new MemoryStream();
            doc.Save(m);
            Put(z, name, m.ToArray());
        });

        var report = Check(path, VendorTrusted);

        Assert.True(Assert.Single(report.Signatures).Trusted, Describe(report));
    }

    // Wrapping: a second Object with the signed Id and a Manifest without the root file.
    [Theory]
    [InlineData("before")]
    [InlineData("after")]
    [InlineData("wrapped")]
    public void A_wrapped_or_doubled_Object_does_not_pass(string mode)
    {
        var path = Write("wrapped.amlx", _vendor);
        Zip(path, z =>
        {
            var entry = z.Entries.First(e => e.FullName.EndsWith(".psdsxs", StringComparison.Ordinal));
            var name = entry.FullName;
            var doc = new XmlDocument { PreserveWhitespace = true };
            doc.Load(new MemoryStream(Read(entry)));
            var ns = new XmlNamespaceManager(doc.NameTable);
            ns.AddNamespace("ds", Ds);
            var original = (XmlElement)doc.SelectSingleNode("/ds:Signature/ds:Object", ns)!;
            var fake = (XmlElement)original.CloneNode(true);
            foreach (var r in fake.SelectNodes("ds:Manifest/ds:Reference", ns)!.Cast<XmlElement>().ToList())
                if (r.GetAttribute("URI").StartsWith("/station.aml?", StringComparison.Ordinal)) r.ParentNode!.RemoveChild(r);
            if (mode == "before") original.ParentNode!.InsertBefore(fake, original);
            else if (mode == "after") original.ParentNode!.AppendChild(fake);
            else
            {
                var wrapper = doc.CreateElement("w", "Wrapper", "urn:x");
                original.ParentNode!.ReplaceChild(wrapper, original);
                wrapper.AppendChild(original);
                doc.DocumentElement!.AppendChild(fake);
            }
            var m = new MemoryStream();
            doc.Save(m);
            Put(z, name, m.ToArray());
            Put(z, "station.aml", Document("Backdoor"));
        });

        Refused(Check(path, VendorTrusted));
    }

    /// <summary>An OPC package signature as PackageSignatures.Sign makes it, with what another tool or an attacker might turn.</summary>
    private static void CustomSign(Package package, X509Certificate2 certificate, bool emptyManifest = false, string? relsSourceType = null)
    {
        var origin = PackageSignatures.OriginPart(package);
        if (origin == null)
        {
            origin = package.CreatePart(new Uri("/package/services/digital-signature/origin.psdsor", UriKind.Relative), PackageSignatures.OriginContentType)!;
            package.CreateRelationship(origin.Uri, TargetMode.Internal, PackageSignatures.OriginRelationship);
        }
        package.Flush();
        var originRelationships = PackUriHelper.GetRelationshipPartUri(origin.Uri);
        var work = new XmlDocument { PreserveWhitespace = true };
        var holder = work.CreateElement("Holder", Ds);
        var manifest = work.CreateElement("Manifest", Ds);
        holder.AppendChild(manifest);
        if (!emptyManifest)
            foreach (var part in package.GetParts().Where(p => p.Uri != originRelationships).OrderBy(p => p.Uri.OriginalString, StringComparer.Ordinal))
            {
                byte[] bytes;
                using (var s = part.GetStream(FileMode.Open, FileAccess.Read)) { var m = new MemoryStream(); s.CopyTo(m); bytes = m.ToArray(); }
                var reference = work.CreateElement("Reference", Ds);
                reference.SetAttribute("URI", $"{part.Uri.OriginalString}?ContentType={part.ContentType}");
                if (PackUriHelper.IsRelationshipPartUri(part.Uri))
                {
                    var transforms = work.CreateElement("Transforms", Ds);
                    if (relsSourceType != null && part.Uri.OriginalString == "/_rels/.rels")
                    {
                        var t = work.CreateElement("Transform", Ds);
                        t.SetAttribute("Algorithm", PackageSignatures.RelationshipTransform);
                        var g = work.CreateElement("mdssi", "RelationshipsGroupReference", OpcDs);
                        g.SetAttribute("SourceType", relsSourceType);
                        t.AppendChild(g);
                        transforms.AppendChild(t);
                        bytes = PackageSignatures.Apply(t, bytes);
                    }
                    var c = work.CreateElement("Transform", Ds);
                    c.SetAttribute("Algorithm", PackageSignatures.C14N);
                    transforms.AppendChild(c);
                    bytes = PackageSignatures.Apply(c, bytes);
                    reference.AppendChild(transforms);
                }
                var dm = work.CreateElement("DigestMethod", Ds);
                dm.SetAttribute("Algorithm", PackageSignatures.Sha256);
                reference.AppendChild(dm);
                var dv = work.CreateElement("DigestValue", Ds);
                dv.InnerText = Convert.ToBase64String(SHA256.HashData(bytes));
                reference.AppendChild(dv);
                manifest.AppendChild(reference);
            }
        var properties = work.CreateElement("SignatureProperties", Ds);
        var property = work.CreateElement("SignatureProperty", Ds);
        property.SetAttribute("Id", "idSignatureTime");
        property.SetAttribute("Target", "#SignatureIdValue");
        var time = work.CreateElement("SignatureTime", OpcDs);
        var format = work.CreateElement("Format", OpcDs);
        format.InnerText = "YYYY-MM-DDThh:mm:ss.sTZD";
        var value = work.CreateElement("Value", OpcDs);
        value.InnerText = "2026-11-17T10:00:00.0+01:00";
        time.AppendChild(format);
        time.AppendChild(value);
        property.AppendChild(time);
        properties.AppendChild(property);
        holder.AppendChild(properties);

        var signed = new SignedXml { SigningKey = certificate.GetRSAPrivateKey() };
        signed.Signature.Id = "SignatureIdValue";
        signed.SignedInfo!.CanonicalizationMethod = PackageSignatures.C14N;
        signed.SignedInfo.SignatureMethod = PackageSignatures.RsaSha256;
        signed.AddObject(new DataObject { Id = "idPackageObject", Data = holder.ChildNodes });
        signed.AddReference(new Reference("#idPackageObject") { Type = "http://www.w3.org/2000/09/xmldsig#Object", DigestMethod = PackageSignatures.Sha256 });
        signed.KeyInfo = new KeyInfo();
        signed.KeyInfo.AddClause(new KeyInfoX509Data(certificate));
        signed.ComputeSignature();
        var output = new XmlDocument { PreserveWhitespace = true };
        output.AppendChild(output.CreateXmlDeclaration("1.0", "utf-8", "yes"));
        output.AppendChild(output.ImportNode(signed.GetXml(), true));
        var signatureUri = PackUriHelper.CreatePartUri(new Uri($"/package/services/digital-signature/xml-signature/{Guid.NewGuid():N}.psdsxs", UriKind.Relative));
        var signaturePart = package.CreatePart(signatureUri, PackageSignatures.SignatureContentType)!;
        using (var s = signaturePart.GetStream(FileMode.Create, FileAccess.Write)) output.Save(s);
        origin.CreateRelationship(signatureUri, TargetMode.Internal, PackageSignatures.SignatureRelationship);
        package.Flush();
    }
}
