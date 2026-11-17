using System.Security.Cryptography.X509Certificates;
using OpcUaAml.Descriptors;

namespace OpcUaAml.Tests;

public sealed class DescriptorKeysTests : IDisposable
{
    private readonly string _folder = Directory.CreateTempSubdirectory("opcuaaml-keys-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_folder, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public void A_new_signer_is_kept_and_found_again()
    {
        var keys = new DescriptorKeys(_folder);

        using var made = keys.CreateSigner("Plant engineering");
        var again = new DescriptorKeys(_folder).OwnSigners();

        Assert.True(made.HasPrivateKey);
        Assert.Equal("CN=Plant engineering", made.Subject);
        var found = Assert.Single(again);
        Assert.Equal(made.Thumbprint, found.Thumbprint);
        Assert.True(found.HasPrivateKey);
    }

    [Fact]
    public void A_Descriptor_signed_with_an_own_key_is_trusted()
    {
        var keys = new DescriptorKeys(_folder);
        using var signer = keys.CreateSigner("Plant engineering");

        Assert.Contains(keys.Trust().Trusted, c => c.Thumbprint == signer.Thumbprint);
    }

    [Fact]
    public void A_certificate_trusted_once_stays_trusted()
    {
        using var other = PackageSignatureTests.RsaCertificate(name: "CN=Vendor");
        using var publicPart = new X509Certificate2(other.Export(X509ContentType.Cert));

        new DescriptorKeys(_folder).Trust(publicPart);
        var trust = new DescriptorKeys(_folder).Trust();

        Assert.Contains(trust.Trusted, c => c.Thumbprint == other.Thumbprint);
        Assert.False(Assert.Single(trust.Trusted).HasPrivateKey);
    }

    [Fact]
    public void Without_a_folder_there_is_nothing_yet()
    {
        var keys = new DescriptorKeys(Path.Combine(_folder, "not-yet"));

        Assert.Empty(keys.OwnSigners());
        Assert.Empty(keys.Trust().Trusted);
    }
}
