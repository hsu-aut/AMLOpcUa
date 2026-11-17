using System.IO;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Windows.Controls;
using Aml.Editor.Plugin.OpcUa;
using OpcUaAml.Descriptors;

namespace PluginTests;

/// <summary>Save as UAFX Descriptor and Check a UAFX Descriptor, without the editor.</summary>
public sealed class DescriptorDialogTests : IDisposable
{
    private readonly string _folder = Directory.CreateTempSubdirectory("plugin-descriptor-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_folder, recursive: true); } catch (IOException) { }
    }

    private static readonly byte[] FxDocument = Encoding.UTF8.GetBytes("""
        <?xml version="1.0" encoding="utf-8"?>
        <CAEXFile SchemaVersion="3.0" FileName="station.aml" xmlns="http://www.dke.de/CAEX">
          <InstanceHierarchy Name="Station">
            <InternalElement Name="Station1" ID="5b1c6a3e-1d0e-4c55-9f37-7c7e0b3f0a01" RefBaseSystemUnitPath="SUC_http://opcfoundation.org/UA/FX/AC//AutomationComponentType" />
          </InstanceHierarchy>
          <SystemUnitClassLib Name="SUC_http://opcfoundation.org/UA/FX/AC/">
            <AdditionalInformation>
              <OpcUaLibInfo xmlns="http://opcfoundation.org/UA/FX/2021/08/OpcUaLibInfo.xsd">
                <OpcUaNamespaceUri>http://opcfoundation.org/UA/FX/AC/</OpcUaNamespaceUri>
                <ModelVersion>1.00.03</ModelVersion>
                <ModelPublicationDate>2025-06-30T00:00:00Z</ModelPublicationDate>
              </OpcUaLibInfo>
            </AdditionalInformation>
          </SystemUnitClassLib>
        </CAEXFile>
        """);

    private static void OnUiThread(Action body)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { body(); }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure != null) throw new Xunit.Sdk.XunitException(failure.ToString());
    }

    private DescriptorReport Written(DescriptorKeys keys, bool trustOwn)
    {
        using var signer = keys.CreateSigner("Plant engineering");
        var path = Path.Combine(_folder, "station.amlx");
        DescriptorWriter.Write(path, new DescriptorContent
        {
            Info = new DescriptorInfo("urn:hsu:mps500:station1", new DescriptorVersion(2, 1, 0, 0), "1.00.03"),
            Root = new DescriptorFile("station.aml", FxDocument),
            Attachments = new[] { new DescriptorFile("manual.pdf", "%PDF-1.7"u8.ToArray()) },
        }, signer, DateTimeOffset.Now);
        return DescriptorChecker.Check(path, trustOwn ? keys.Trust() : DescriptorTrust.None);
    }

    [Fact]
    public void The_summary_names_the_Descriptor_its_files_and_its_signer()
    {
        var keys = new DescriptorKeys(Path.Combine(_folder, "keys"));
        var report = Written(keys, trustOwn: true);

        var summary = DescriptorSummary.Of(report);

        Assert.Contains("urn:hsu:mps500:station1", summary);
        Assert.Contains("2.1.0.0", summary);
        Assert.Contains("1.00.03", summary);
        Assert.Contains("/station.aml", summary);
        Assert.Contains("Plant engineering", summary);
        Assert.Null(DescriptorSummary.SignerToTrust(report));
    }

    [Fact]
    public void Trusting_is_offered_for_a_valid_signature_nobody_trusts_yet()
    {
        var keys = new DescriptorKeys(Path.Combine(_folder, "keys"));
        var report = Written(keys, trustOwn: false);

        var signer = DescriptorSummary.SignerToTrust(report);

        Assert.NotNull(signer);
        Assert.Equal("CN=Plant engineering", signer!.Subject);
    }

    [Fact]
    public void Trusting_is_not_offered_for_a_signature_that_does_not_verify()
    {
        var keys = new DescriptorKeys(Path.Combine(_folder, "keys"));
        Written(keys, trustOwn: false);
        var path = Path.Combine(_folder, "station.amlx");
        using (var package = System.IO.Packaging.Package.Open(path, FileMode.Open, FileAccess.ReadWrite))
        using (var s = package.GetPart(new Uri("/attachments/manual.pdf", UriKind.Relative)).GetStream(FileMode.Create, FileAccess.Write))
            s.Write("%PDF-1.7 changed"u8);

        Assert.Null(DescriptorSummary.SignerToTrust(DescriptorChecker.Check(path)));
    }

    [Fact]
    public void The_window_offers_the_FX_version_of_the_document_and_the_own_signers()
    {
        var keys = new DescriptorKeys(Path.Combine(_folder, "keys"));
        using var own = keys.CreateSigner("Plant engineering");
        OnUiThread(() =>
        {
            var window = new DescriptorWindow("station", FxDocument, keys);

            Assert.Equal("1.00.03", window.FxVersion.Text);
            Assert.Equal("1.0.0.0", window.Version.Text);
            Assert.StartsWith("urn:", window.Identifier.Text);
            Assert.Contains(window.Signers.Items.OfType<ComboBoxItem>(), i => i.Tag is X509Certificate2 c && c.Thumbprint == own.Thumbprint);
            window.Close();
        });
    }

    [Theory]
    [InlineData("not a uri", "1.0", "1.00.03")]
    [InlineData("urn:a", "1.x", "1.00.03")]
    [InlineData("urn:a", "1.0", "")]
    public void The_window_refuses_what_the_manifest_cannot_hold(string identifier, string version, string fx)
    {
        var keys = new DescriptorKeys(Path.Combine(_folder, "keys"));
        using var own = keys.CreateSigner("Plant engineering");
        OnUiThread(() =>
        {
            var window = new DescriptorWindow("station", FxDocument, keys);
            window.Identifier.Text = identifier;
            window.Version.Text = version;
            window.FxVersion.Text = fx;

            Assert.False(window.Accept());
            Assert.Null(window.Info);
            window.Close();
        });
    }

    [Fact]
    public void Without_a_signer_the_window_does_not_accept()
    {
        var keys = new DescriptorKeys(Path.Combine(_folder, "no-keys"));
        OnUiThread(() =>
        {
            var window = new DescriptorWindow("station", FxDocument, keys, includeStore: false);

            Assert.False(window.Accept());
            window.Close();
        });
    }

    [Fact]
    public void The_window_gives_the_manifest_the_signer_and_the_attachments()
    {
        var keys = new DescriptorKeys(Path.Combine(_folder, "keys"));
        using var own = keys.CreateSigner("Plant engineering");
        var manual = Path.Combine(_folder, "manual.pdf");
        File.WriteAllText(manual, "%PDF-1.7");
        OnUiThread(() =>
        {
            var window = new DescriptorWindow("station", FxDocument, keys, includeStore: false);
            window.Identifier.Text = "urn:hsu:mps500:station1";
            window.Version.Text = "2.1";
            window.AddAttachment(manual);

            Assert.True(window.Accept());
            Assert.Equal(new DescriptorInfo("urn:hsu:mps500:station1", new DescriptorVersion(2, 1, 0, 0), "1.00.03"), window.Info);
            Assert.Equal(own.Thumbprint, window.Signer!.Thumbprint);
            Assert.Equal(new[] { manual }, window.Attachments);
            window.Close();
        });
    }
}
