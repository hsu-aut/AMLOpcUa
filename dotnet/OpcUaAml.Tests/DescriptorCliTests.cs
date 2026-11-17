using System.Security.Cryptography.X509Certificates;
using OpcUaAml.Descriptors;

namespace OpcUaAml.Tests;

/// <summary>uaaml descriptor: a signing certificate, a Descriptor from a document, and its check.</summary>
public class DescriptorCliTests
{
    private const string Password = "pfx-pass";

    private static string WriteDocument(Uaaml uaaml)
    {
        var path = Path.Combine(uaaml.Folder, "station.aml");
        File.WriteAllBytes(path, DescriptorTests.FxDocument());
        return path;
    }

    private static async Task<(string Pfx, string Cer)> CertificateAsync(Uaaml uaaml)
    {
        var pfx = Path.Combine(uaaml.Folder, "signer.pfx");
        uaaml.Input = Password + Environment.NewLine + Password + Environment.NewLine;
        var run = await uaaml.RunAsync("descriptor", "certificate", "CN=HSU Controls", "-o", pfx);
        Assert.True(run.Exit == 0, run.Error);
        return (pfx, Path.ChangeExtension(pfx, ".cer"));
    }

    [Fact]
    public async Task A_certificate_made_for_signing_is_written_with_its_public_part()
    {
        using var uaaml = new Uaaml();

        var (pfx, cer) = await CertificateAsync(uaaml);

        using var withKey = new X509Certificate2(pfx, Password);
        Assert.True(withKey.HasPrivateKey);
        Assert.Equal("CN=HSU Controls", withKey.Subject);
        Assert.True(withKey.Extensions.OfType<X509KeyUsageExtension>().Single().KeyUsages.HasFlag(X509KeyUsageFlags.DigitalSignature));
        using var publicPart = new X509Certificate2(cer);
        Assert.False(publicPart.HasPrivateKey);
        Assert.Equal(withKey.Thumbprint, publicPart.Thumbprint);
    }

    [Fact]
    public async Task A_Descriptor_made_from_a_document_checks_clean_when_its_signer_is_trusted()
    {
        using var uaaml = new Uaaml();
        var (pfx, cer) = await CertificateAsync(uaaml);
        var document = WriteDocument(uaaml);
        var manual = Path.Combine(uaaml.Folder, "manual.pdf");
        File.WriteAllText(manual, "%PDF-1.7 a manual");
        var output = Path.Combine(uaaml.Folder, "station.amlx");

        uaaml.Input = Password + Environment.NewLine;
        var create = await uaaml.RunAsync("descriptor", "create", document, "-o", output, "--id", "urn:hsu:mps500:station1",
            "--version", "1.2", "--cert", pfx, "--attach", manual);
        uaaml.Input = "";
        var check = await uaaml.RunAsync("descriptor", "check", output, "--trust", cer);

        Assert.True(create.Exit == 0, create.Error);
        Assert.True(check.Exit == 0, check.Out + check.Error);
        Assert.Contains("urn:hsu:mps500:station1", check.Out);
        Assert.Contains("1.2.0.0", check.Out);
        Assert.Contains("1.00.03", check.Out);
        Assert.Contains("/attachments/manual.pdf", check.Out);
        Assert.Contains("HSU Controls", check.Out);
        Assert.Contains("0 error(s), 0 warning(s)", check.Out);
    }

    [Fact]
    public async Task Without_trust_the_signer_is_named_but_the_check_passes()
    {
        using var uaaml = new Uaaml();
        var (pfx, _) = await CertificateAsync(uaaml);
        var output = Path.Combine(uaaml.Folder, "station.amlx");
        uaaml.Input = Password + Environment.NewLine;
        await uaaml.RunAsync("descriptor", "create", WriteDocument(uaaml), "-o", output, "--id", "urn:a", "--cert", pfx);
        uaaml.Input = "";

        var check = await uaaml.RunAsync("descriptor", "check", output);

        Assert.Equal(0, check.Exit);
        Assert.Contains(DescriptorRules.Trust, check.Out);
        Assert.Contains("0 error(s), 1 warning(s)", check.Out);
    }

    [Fact]
    public async Task A_changed_Descriptor_fails_the_check()
    {
        using var uaaml = new Uaaml();
        var (pfx, cer) = await CertificateAsync(uaaml);
        var output = Path.Combine(uaaml.Folder, "station.amlx");
        uaaml.Input = Password + Environment.NewLine;
        await uaaml.RunAsync("descriptor", "create", WriteDocument(uaaml), "-o", output, "--id", "urn:a", "--cert", pfx);
        uaaml.Input = "";
        using (var package = System.IO.Packaging.Package.Open(output, FileMode.Open, FileAccess.ReadWrite))
        using (var s = package.GetPart(new Uri("/station.aml", UriKind.Relative)).GetStream(FileMode.Open, FileAccess.ReadWrite))
        {
            s.Seek(0, SeekOrigin.End);
            s.Write("<!-- changed -->"u8);
        }

        var check = await uaaml.RunAsync("descriptor", "check", output, "--trust", cer);

        Assert.Equal(1, check.Exit);
        Assert.Contains(DescriptorRules.SignatureInvalid, check.Out);
    }

    [Fact]
    public async Task A_document_without_FX_library_needs_the_FX_version()
    {
        using var uaaml = new Uaaml();
        var (pfx, _) = await CertificateAsync(uaaml);
        var document = Path.Combine(uaaml.Folder, "plain.aml");
        File.WriteAllBytes(document, DescriptorTests.FxDocument(fx: false));
        uaaml.Input = Password + Environment.NewLine;

        var run = await uaaml.RunAsync("descriptor", "create", document, "-o", Path.Combine(uaaml.Folder, "x.amlx"), "--id", "urn:a", "--cert", pfx);

        Assert.Equal(1, run.Exit);
        Assert.Contains("--fx-version", run.Error);
    }

    [Fact]
    public async Task A_wrong_password_is_named()
    {
        using var uaaml = new Uaaml();
        var (pfx, _) = await CertificateAsync(uaaml);
        uaaml.Input = "not the password" + Environment.NewLine;

        var run = await uaaml.RunAsync("descriptor", "create", WriteDocument(uaaml), "-o", Path.Combine(uaaml.Folder, "x.amlx"),
            "--id", "urn:a", "--cert", pfx);

        Assert.Equal(1, run.Exit);
        Assert.Contains("password", run.Error, StringComparison.OrdinalIgnoreCase);
    }
}
