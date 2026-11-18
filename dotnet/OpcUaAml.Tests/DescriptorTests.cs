using System.IO.Compression;
using System.IO.Packaging;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Xml.Linq;
using OpcUaAml.Checks;
using OpcUaAml.Descriptors;
using OpcUaAml.Import;

namespace OpcUaAml.Tests;

public sealed class DescriptorTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 11, 17, 10, 0, 0, TimeSpan.FromHours(1));
    private readonly string _folder = Directory.CreateTempSubdirectory("opcuaaml-descriptor-").FullName;
    private readonly X509Certificate2 _signer = PackageSignatureTests.RsaCertificate(name: "CN=HSU Controls");

    public void Dispose()
    {
        _signer.Dispose();
        try { Directory.Delete(_folder, recursive: true); } catch (IOException) { }
    }

    /// <summary>A small OPC UA FX information model: an instance of AutomationComponentType and its library.</summary>
    internal static byte[] FxDocument(bool fx = true, string fxVersion = "1.00.03")
    {
        var ns = fx ? "http://opcfoundation.org/UA/FX/AC/" : "http://example.org/Plant/";
        var xml = $"""
            <?xml version="1.0" encoding="utf-8"?>
            <CAEXFile SchemaVersion="3.0" FileName="station.aml" xmlns="http://www.dke.de/CAEX">
              <SuperiorStandardVersion>AutomationML 2.10</SuperiorStandardVersion>
              <SourceDocumentInformation OriginName="DescriptorTests" OriginID="tests" OriginVersion="1" LastWritingDateTime="2026-11-17T10:00:00" />
              <InstanceHierarchy Name="Station">
                <InternalElement Name="Station1" ID="5b1c6a3e-1d0e-4c55-9f37-7c7e0b3f0a01" RefBaseSystemUnitPath="SUC_{ns}/AutomationComponentType" />
              </InstanceHierarchy>
              <SystemUnitClassLib Name="SUC_{ns}">
                <AdditionalInformation>
                  <OpcUaLibInfo xmlns="http://opcfoundation.org/UA/FX/2021/08/OpcUaLibInfo.xsd">
                    <OpcUaNamespaceUri>{ns}</OpcUaNamespaceUri>
                    <ModelVersion>{fxVersion}</ModelVersion>
                    <ModelPublicationDate>2025-06-30T00:00:00Z</ModelPublicationDate>
                  </OpcUaLibInfo>
                </AdditionalInformation>
                <SystemUnitClass Name="AutomationComponentType" ID="5b1c6a3e-1d0e-4c55-9f37-7c7e0b3f0a02" />
              </SystemUnitClassLib>
            </CAEXFile>
            """;
        return Encoding.UTF8.GetBytes(xml);
    }

    private static DescriptorContent Content(byte[]? root = null, params DescriptorFile[] attachments) => new()
    {
        Info = new DescriptorInfo("urn:hsu:mps500:station1", new DescriptorVersion(1, 0, 0, 0), "1.00.03"),
        Root = new DescriptorFile("station.aml", root ?? FxDocument()),
        Attachments = attachments,
    };

    private static readonly DescriptorFile Manual = new("manual.pdf", "%PDF-1.7 a manual"u8.ToArray());

    private string Write(DescriptorContent? content = null, X509Certificate2? signer = null, DateTimeOffset? time = null,
        IEnumerable<X509Certificate2>? chain = null, string name = "station.amlx")
    {
        var path = Path.Combine(_folder, name);
        DescriptorWriter.Write(path, content ?? Content(null, Manual), signer ?? _signer, time ?? Now, chain);
        return path;
    }

    private DescriptorReport Check(string path, bool trusted = true) =>
        DescriptorChecker.Check(path, trusted ? new DescriptorTrust(new[] { _signer }) : DescriptorTrust.None, Now.AddHours(1));

    /// <summary>Changes a written Descriptor and signs it anew, so only the change is found.</summary>
    private void Resign(string path, Action<Package> change)
    {
        using var package = Package.Open(path, FileMode.Open, FileAccess.ReadWrite);
        var origin = PackageSignatures.OriginPart(package)!;
        foreach (var r in origin.GetRelationships().ToList())
            package.DeletePart(PackUriHelper.ResolvePartUri(origin.Uri, r.TargetUri));
        foreach (var r in package.GetRelationshipsByType(PackageSignatures.OriginRelationship).ToList())
            package.DeleteRelationship(r.Id);
        package.DeletePart(origin.Uri);
        change(package);
        PackageSignatures.Sign(package, _signer, Now);
    }

    private static IEnumerable<string> Rules(DescriptorReport report) => report.Findings.Select(f => f.Rule);

    private static string Describe(DescriptorReport report) => string.Join(Environment.NewLine, report.Findings);

    [Fact]
    public void A_Descriptor_written_from_a_document_checks_clean()
    {
        var path = Write();

        var report = Check(path);

        Assert.True(report.Findings.Count == 0, Describe(report));
        Assert.Equal(new DescriptorInfo("urn:hsu:mps500:station1", new DescriptorVersion(1, 0, 0, 0), "1.00.03"), report.Info);
        Assert.Equal("/manifest.xml", report.ManifestPart);
        Assert.Equal(new[] { "/station.aml" }, report.RootFiles);
        Assert.Equal(new[] { "/attachments/manual.pdf" }, report.Attachments);
        var verdict = Assert.Single(report.Signatures);
        Assert.True(verdict.Signature.ValueValid);
        Assert.True(verdict.Trusted);
        Assert.Equal(_signer.Thumbprint, verdict.Signature.Signer!.Thumbprint);
    }

    [Fact]
    public void A_Descriptor_opens_as_an_AML_container()
    {
        var path = Write();

        var document = AmlFiles.Load(path);

        Assert.Equal("Station1", document.CAEXFile.InstanceHierarchy["Station"]!.InternalElement.Single().Name);
    }

    [Fact]
    public void The_manifest_root_and_attachments_are_named_by_relationships()
    {
        var path = Write();

        using var package = Package.Open(path, FileMode.Open, FileAccess.Read);
        var manifest = Assert.Single(package.GetRelationshipsByType(DescriptorRelationships.Manifest));
        Assert.Equal("/manifest.xml", manifest.TargetUri.OriginalString);
        var root = Assert.Single(package.GetRelationshipsByType(DescriptorRelationships.RootDocument));
        Assert.Equal("/station.aml", root.TargetUri.OriginalString);
        Assert.Equal("application/automationml-aml+xml", package.GetPart(root.TargetUri).ContentType);
        var rootPart = package.GetPart(new Uri("/station.aml", UriKind.Relative));
        var attachment = Assert.Single(rootPart.GetRelationshipsByType(DescriptorRelationships.AnyContent));
        Assert.Equal("/attachments/manual.pdf", PackUriHelper.ResolvePartUri(rootPart.Uri, attachment.TargetUri).OriginalString);
        Assert.Equal("text/xml", package.GetPart(manifest.TargetUri).ContentType);
    }

    [Fact]
    public void Content_types_list_only_the_extensions_in_use()
    {
        var path = Write();

        using var zip = ZipFile.OpenRead(path);
        var types = XDocument.Load(zip.GetEntry("[Content_Types].xml")!.Open());
        var listed = types.Root!.Elements().Where(e => e.Name.LocalName == "Default")
            .Select(e => ((string)e.Attribute("Extension")!).ToLowerInvariant()).OrderBy(e => e);
        var used = zip.Entries.Where(e => e.FullName != "[Content_Types].xml")
            .Select(e => Path.GetExtension(e.FullName).TrimStart('.').ToLowerInvariant()).Distinct().OrderBy(e => e);

        Assert.Equal(used, listed);
    }

    [Fact]
    public void The_OPC_UA_FX_version_is_read_from_the_FX_library_of_the_document()
    {
        Assert.Equal("1.00.04", DescriptorWriter.FxVersionOf(FxDocument(fxVersion: "1.00.04")));
        Assert.Null(DescriptorWriter.FxVersionOf(FxDocument(fx: false)));
    }

    [Fact]
    public void An_attached_Descriptor_is_embedded_and_checked_with_it()
    {
        var vendor = Write(Content(null), name: "vendor.amlx");
        var content = Content(null, Manual, new DescriptorFile("vendor.amlx", File.ReadAllBytes(vendor)));
        var path = Write(content);

        var report = Check(path);

        Assert.True(report.Findings.Count == 0, Describe(report));
        Assert.Equal(new[] { "/attachments/manual.pdf" }, report.Attachments);
        var embedded = Assert.Single(report.Embedded);
        Assert.Equal("urn:hsu:mps500:station1", embedded.Info!.Identifier);
        Assert.Equal("/descriptors/vendor.amlx", embedded.Name);
        using var package = Package.Open(path, FileMode.Open, FileAccess.Read);
        var root = package.GetPart(new Uri("/station.aml", UriKind.Relative));
        Assert.Single(root.GetRelationshipsByType(DescriptorRelationships.EmbeddedDescriptor));
    }

    [Fact]
    public void A_flaw_of_an_embedded_Descriptor_is_named_with_its_path()
    {
        var vendor = Write(Content(null, Manual), name: "vendor.amlx");
        using (var package = Package.Open(vendor, FileMode.Open, FileAccess.ReadWrite))
        using (var s = package.GetPart(new Uri("/attachments/manual.pdf", UriKind.Relative)).GetStream(FileMode.Create, FileAccess.Write))
            s.Write("%PDF-1.7 not the vendor's manual"u8);
        var path = Write(Content(null, new DescriptorFile("vendor.amlx", File.ReadAllBytes(vendor))));

        var report = Check(path);

        var finding = Assert.Single(report.Findings);
        Assert.Equal(DescriptorRules.SignatureInvalid, finding.Rule);
        Assert.Equal("/descriptors/vendor.amlx!/attachments/manual.pdf", finding.ElementPath);
    }

    [Fact]
    public void An_unsigned_Descriptor_is_named()
    {
        var path = Write();
        using (var package = Package.Open(path, FileMode.Open, FileAccess.ReadWrite))
        {
            var origin = PackageSignatures.OriginPart(package)!;
            foreach (var r in origin.GetRelationships().ToList())
                package.DeletePart(PackUriHelper.ResolvePartUri(origin.Uri, r.TargetUri));
            foreach (var r in package.GetRelationshipsByType(PackageSignatures.OriginRelationship).ToList())
                package.DeleteRelationship(r.Id);
            package.DeletePart(origin.Uri);
        }

        Assert.Contains(DescriptorRules.NoSignature, Rules(Check(path)));
    }

    [Fact]
    public void A_file_changed_after_signing_is_named()
    {
        var path = Write();
        using (var package = Package.Open(path, FileMode.Open, FileAccess.ReadWrite))
        using (var s = package.GetPart(new Uri("/attachments/manual.pdf", UriKind.Relative)).GetStream(FileMode.Create, FileAccess.Write))
            s.Write("%PDF-1.7 another manual"u8);

        var report = Check(path);

        var finding = Assert.Single(report.Findings);
        Assert.Equal(DescriptorRules.SignatureInvalid, finding.Rule);
        Assert.Equal(Severity.Error, finding.Severity);
        Assert.Equal("/attachments/manual.pdf", finding.ElementPath);
    }

    [Fact]
    public void A_Descriptor_without_manifest_is_named()
    {
        var path = Write();
        Resign(path, p =>
        {
            foreach (var r in p.GetRelationshipsByType(DescriptorRelationships.Manifest).ToList()) p.DeleteRelationship(r.Id);
            p.DeletePart(new Uri("/manifest.xml", UriKind.Relative));
        });

        var report = Check(path);

        // [Content_Types].xml still lists .xml: the package library keeps a default it no longer needs.
        var finding = Assert.Single(report.Findings, f => f.Rule != DescriptorRules.ContentTypes);
        Assert.Equal((DescriptorRules.Manifest, Severity.Error), (finding.Rule, finding.Severity));
        Assert.Null(report.Info);
    }

    [Fact]
    public void A_second_manifest_is_named()
    {
        var path = Write();
        Resign(path, p =>
        {
            var second = p.CreatePart(new Uri("/other/manifest.xml", UriKind.Relative), "text/xml")!;
            using (var s = second.GetStream()) new DescriptorInfo("urn:other", default, "1.00.03").ToXml().Save(s);
            p.CreateRelationship(second.Uri, TargetMode.Internal, DescriptorRelationships.Manifest);
        });

        var report = Check(path);

        var finding = Assert.Single(report.Findings);
        Assert.Equal((DescriptorRules.Manifest, Severity.Error), (finding.Rule, finding.Severity));
        Assert.Contains("2", finding.Args);
    }

    [Fact]
    public void A_manifest_found_by_its_content_under_another_relationship_type_is_a_warning()
    {
        var path = Write();
        Resign(path, p =>
        {
            var relation = p.GetRelationshipsByType(DescriptorRelationships.Manifest).Single();
            p.DeleteRelationship(relation.Id);
            p.CreateRelationship(relation.TargetUri, TargetMode.Internal, "http://vendor.example/relationship/UafxManifest");
        });

        var report = Check(path);

        var finding = Assert.Single(report.Findings);
        Assert.Equal((DescriptorRules.Manifest, Severity.Warning), (finding.Rule, finding.Severity));
        Assert.NotNull(report.Info);
    }

    [Fact]
    public void A_manifest_departing_from_Annex_J_is_named()
    {
        var path = Write();
        Resign(path, p =>
        {
            using var s = p.GetPart(new Uri("/manifest.xml", UriKind.Relative)).GetStream(FileMode.Create, FileAccess.Write);
            XDocument.Parse("""<DescriptorInfo xmlns="http://opcfoundation.org/UA/FX/2021/08/DescriptorInfo.xsd"><DescriptorIdentifier>urn:a</DescriptorIdentifier></DescriptorInfo>""").Save(s);
        });

        var report = Check(path);

        Assert.All(report.Findings, f => Assert.Equal(DescriptorRules.Manifest, f.Rule));
        Assert.Contains(report.Findings, f => f.Format == DescriptorTexts.ManifestNoVersion);
        Assert.Contains(report.Findings, f => f.Format == DescriptorTexts.ManifestNoFxVersion);
        Assert.All(report.Findings, f => Assert.Equal("/manifest.xml", f.ElementPath));
    }

    [Fact]
    public void A_file_no_Root_AML_file_leads_to_is_named()
    {
        var path = Write();
        Resign(path, p =>
        {
            var lost = p.CreatePart(new Uri("/attachments/lost.txt", UriKind.Relative), "text/plain")!;
            using var s = lost.GetStream();
            s.Write("nobody refers to me"u8);
        });

        var finding = Assert.Single(Check(path).Findings);

        Assert.Equal((DescriptorRules.Unreachable, "/attachments/lost.txt"), (finding.Rule, finding.ElementPath));
    }

    [Fact]
    public void A_relationship_to_a_missing_file_is_named()
    {
        var path = Write();
        Resign(path, p => p.GetPart(new Uri("/station.aml", UriKind.Relative))
            .CreateRelationship(new Uri("/attachments/gone.pdf", UriKind.Relative), TargetMode.Internal, DescriptorRelationships.AnyContent));

        var finding = Assert.Single(Check(path).Findings);

        Assert.Equal((DescriptorRules.BrokenRelationship, "/station.aml"), (finding.Rule, finding.ElementPath));
    }

    [Fact]
    public void Relationships_in_a_circle_are_named()
    {
        var path = Write();
        Resign(path, p =>
        {
            var library = p.CreatePart(new Uri("/library.aml", UriKind.Relative), "application/automationml-aml+xml")!;
            using (var s = library.GetStream()) s.Write(FxDocument());
            var root = p.GetPart(new Uri("/station.aml", UriKind.Relative));
            root.CreateRelationship(library.Uri, TargetMode.Internal, DescriptorRelationships.Library);
            library.CreateRelationship(root.Uri, TargetMode.Internal, DescriptorRelationships.Library);
        });

        var finding = Assert.Single(Check(path).Findings);

        Assert.Equal(DescriptorRules.Circular, finding.Rule);
        Assert.Contains("/library.aml", finding.Message);
    }

    [Fact]
    public void An_extension_no_file_has_in_the_content_types_is_a_warning()
    {
        var path = Write();
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Update))
        {
            var entry = zip.GetEntry("[Content_Types].xml")!;
            XDocument types;
            using (var s = entry.Open()) types = XDocument.Load(s);
            XNamespace ct = "http://schemas.openxmlformats.org/package/2006/content-types";
            types.Root!.Add(new XElement(ct + "Default", new XAttribute("Extension", "jpg"), new XAttribute("ContentType", "image/jpeg")));
            entry.Delete();
            using var w = zip.CreateEntry("[Content_Types].xml").Open();
            types.Save(w);
        }

        var finding = Assert.Single(Check(path).Findings);

        Assert.Equal((DescriptorRules.ContentTypes, Severity.Warning), (finding.Rule, finding.Severity));
        Assert.Contains("jpg", finding.Args);
    }

    [Fact]
    public void A_file_added_after_signing_is_outside_the_signature()
    {
        var path = Write();
        using (var package = Package.Open(path, FileMode.Open, FileAccess.ReadWrite))
        {
            var photo = package.CreatePart(new Uri("/attachments/photo.png", UriKind.Relative), "image/png")!;
            using (var s = photo.GetStream()) s.Write(new byte[] { 0x89, 0x50, 0x4E, 0x47 });
            package.GetPart(new Uri("/station.aml", UriKind.Relative))
                .CreateRelationship(photo.Uri, TargetMode.Internal, DescriptorRelationships.AnyContent);
        }

        var report = Check(path);

        Assert.Contains(report.Findings, f => f.Rule == DescriptorRules.NotSigned && f.ElementPath == "/attachments/photo.png");
        Assert.Contains(report.Findings, f => f.Rule == DescriptorRules.SignatureInvalid && f.ElementPath == "/_rels/station.aml.rels");
    }

    // OPC 10000-83 7.8.2: validation fails when neither the signer nor a CA of its
    // chain is trusted. It was a warning, so anyone's signature passed the CLI.
    [Fact]
    public void A_signer_nobody_trusts_is_an_error_though_the_signature_holds()
    {
        var path = Write();

        var report = Check(path, trusted: false);

        var finding = Assert.Single(report.Findings);
        Assert.Equal((DescriptorRules.Trust, Severity.Error, DescriptorTexts.Untrusted), (finding.Rule, finding.Severity, finding.Format));
        var verdict = Assert.Single(report.Signatures);
        Assert.True(verdict.Valid);
        Assert.False(verdict.Trusted);
    }

    [Fact]
    public void A_key_shorter_than_the_policies_allow_is_named()
    {
        using var weak = PackageSignatureTests.RsaCertificate(1024, "CN=Weak");
        var path = Write(signer: weak);

        var report = DescriptorChecker.Check(path, new DescriptorTrust(new[] { weak }), Now);

        var finding = Assert.Single(report.Findings);
        Assert.Equal(DescriptorRules.Policy, finding.Rule);
        Assert.Contains("1024", finding.Args);
    }

    [Fact]
    public void A_certificate_not_for_signing_is_named()
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=Encryption only", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyEncipherment, critical: true));
        using var made = request.CreateSelfSigned(Now.AddDays(-1), Now.AddYears(1));
        using var cert = new X509Certificate2(made.Export(X509ContentType.Pfx), (string?)null, X509KeyStorageFlags.Exportable);
        var path = Write(signer: cert);

        var report = DescriptorChecker.Check(path, new DescriptorTrust(new[] { cert }), Now);

        var finding = Assert.Single(report.Findings);
        Assert.Equal((DescriptorRules.Usage, DescriptorTexts.NotForSigning), (finding.Rule, finding.Format));
    }

    [Fact]
    public void A_CA_certificate_that_signs_a_Descriptor_is_named()
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=Some CA", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyCertSign, critical: true));
        using var made = request.CreateSelfSigned(Now.AddDays(-1), Now.AddYears(1));
        using var cert = new X509Certificate2(made.Export(X509ContentType.Pfx), (string?)null, X509KeyStorageFlags.Exportable);
        var path = Write(signer: cert);

        var report = DescriptorChecker.Check(path, new DescriptorTrust(new[] { cert }), Now);

        var finding = Assert.Single(report.Findings);
        Assert.Equal((DescriptorRules.Usage, DescriptorTexts.IsCa), (finding.Rule, finding.Format));
    }

    [Fact]
    public void A_signature_made_after_its_certificate_expired_is_named()
    {
        var path = Write(time: _signer.NotAfter.AddDays(2));

        var report = Check(path);

        Assert.Contains(report.Findings, f => f.Rule == DescriptorRules.Validity);
        Assert.All(report.Findings, f => Assert.Equal(DescriptorRules.Validity, f.Rule));
    }

    private static (X509Certificate2 Ca, X509Certificate2 Leaf) CaAndLeaf()
    {
        using var caKey = RSA.Create(3072);
        var caRequest = new CertificateRequest("CN=HSU Root CA", caKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        caRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        caRequest.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
        var ca = caRequest.CreateSelfSigned(Now.AddDays(-10), Now.AddYears(5));

        using var leafKey = RSA.Create(2048);
        var leafRequest = new CertificateRequest("CN=Engineering tool", leafKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        leafRequest.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
        leafRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        using var issued = leafRequest.Create(ca, Now.AddDays(-1), Now.AddYears(1), new byte[] { 1, 2, 3, 4 });
        using var withKey = issued.CopyWithPrivateKey(leafKey);
        var leaf = new X509Certificate2(withKey.Export(X509ContentType.Pfx), (string?)null, X509KeyStorageFlags.Exportable);
        return (ca, leaf);
    }

    [Fact]
    public void A_signer_issued_by_a_trusted_CA_is_trusted()
    {
        var (ca, leaf) = CaAndLeaf();
        using (ca) using (leaf)
        {
            var path = Write(signer: leaf);
            using var caPublic = new X509Certificate2(ca.Export(X509ContentType.Cert));

            var report = DescriptorChecker.Check(path, new DescriptorTrust(new[] { caPublic }), Now);

            // The test CA names no revocation list: that it is not revoked cannot be known.
            var finding = Assert.Single(report.Findings);
            Assert.Equal((Severity.Warning, DescriptorTexts.RevocationUnknown), (finding.Severity, finding.Format));
            Assert.True(Assert.Single(report.Signatures).Trusted);
        }
    }

    [Fact]
    public void A_chain_that_cannot_be_built_is_named()
    {
        var (ca, leaf) = CaAndLeaf();
        using (ca) using (leaf)
        {
            var path = Write(signer: leaf);

            var report = DescriptorChecker.Check(path, DescriptorTrust.None, Now);

            var finding = Assert.Single(report.Findings);
            Assert.Equal((DescriptorRules.Trust, Severity.Error, DescriptorTexts.ChainIncomplete), (finding.Rule, finding.Severity, finding.Format));
        }
    }

    [Fact]
    public void A_signature_carrying_the_chain_is_trusted_through_its_root()
    {
        var (ca, leaf) = CaAndLeaf();
        using (ca) using (leaf)
        {
            using var caPublic = new X509Certificate2(ca.Export(X509ContentType.Cert));
            var path = Write(signer: leaf, chain: new[] { caPublic });

            var report = DescriptorChecker.Check(path, new DescriptorTrust(new[] { caPublic }), Now);

            Assert.Equal(DescriptorTexts.RevocationUnknown, Assert.Single(report.Findings).Format);
            Assert.True(Assert.Single(report.Signatures).Trusted);
            Assert.Equal(2, Assert.Single(report.Signatures).Signature.Certificates.Count);
        }
    }

    [Fact]
    public void A_file_that_is_no_container_is_named()
    {
        var path = Path.Combine(_folder, "broken.amlx");
        File.WriteAllText(path, "not a zip");

        var finding = Assert.Single(DescriptorChecker.Check(path).Findings);

        Assert.Equal((DescriptorRules.NotAPackage, Severity.Error), (finding.Rule, finding.Severity));
    }

    [Fact]
    public void A_root_document_without_OPC_UA_FX_library_is_a_warning()
    {
        var path = Write(Content(FxDocument(fx: false)));

        var finding = Assert.Single(Check(path).Findings);

        Assert.Equal((DescriptorRules.Root, Severity.Warning, "/station.aml"), (finding.Rule, finding.Severity, finding.ElementPath));
    }

    [Fact]
    public void Two_attachments_of_one_name_both_keep_their_content()
    {
        var path = Write(Content(null, Manual, new DescriptorFile("manual.pdf", "%PDF-1.7 the second"u8.ToArray())));

        var report = Check(path);

        Assert.True(report.Findings.Count == 0, Describe(report));
        Assert.Equal(2, report.Attachments.Count);
    }
}
