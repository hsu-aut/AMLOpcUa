// The keys the plugin signs Descriptors with and the certificates it trusts
// when it checks them (OPC 10000-83 8.3.4, 7.8.2), each in a folder of its
// own below %LOCALAPPDATA%\AMLOpcUa\descriptors:
//
//   signers\<thumbprint>.pfx    own certificates with their keys
//   trusted\<thumbprint>.der    certificates trusted by the user
//
// A signing key is kept like the plugin's OPC UA application key: in the
// user's profile, readable by the user only. What one signs oneself is trusted.

using System.Security.Cryptography.X509Certificates;

namespace OpcUaAml.Descriptors;

public sealed class DescriptorKeys
{
    public static string DefaultFolder { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AMLOpcUa", "descriptors");

    public DescriptorKeys(string? folder = null) => Folder = folder ?? DefaultFolder;

    public string Folder { get; }

    private string Signers => Path.Combine(Folder, "signers");

    private string Trusted => Path.Combine(Folder, "trusted");

    /// <summary>The own certificates with their keys, newest first.</summary>
    public IReadOnlyList<X509Certificate2> OwnSigners()
    {
        if (!Directory.Exists(Signers)) return Array.Empty<X509Certificate2>();
        var found = new List<X509Certificate2>();
        foreach (var file in Directory.EnumerateFiles(Signers, "*.pfx"))
        {
            try { found.Add(new X509Certificate2(File.ReadAllBytes(file), (string?)null, X509KeyStorageFlags.EphemeralKeySet)); }
            catch (System.Security.Cryptography.CryptographicException) { }
        }
        return found.OrderByDescending(c => c.NotBefore).ToList();
    }

    /// <summary>Makes a self-signed certificate for signing (RSA 2048, three years) and keeps it with its key.</summary>
    public X509Certificate2 CreateSigner(string name)
    {
        var certificate = DescriptorCertificates.CreateSelfSigned(name, DateTimeOffset.Now);
        Directory.CreateDirectory(Signers);
        File.WriteAllBytes(Path.Combine(Signers, certificate.Thumbprint + ".pfx"), certificate.Export(X509ContentType.Pfx));
        return certificate;
    }

    /// <summary>Trusts a certificate from now on: the signer of a Descriptor, or a CA that issues signers.</summary>
    public void Trust(X509Certificate2 certificate)
    {
        Directory.CreateDirectory(Trusted);
        File.WriteAllBytes(Path.Combine(Trusted, certificate.Thumbprint + ".der"), certificate.Export(X509ContentType.Cert));
    }

    /// <summary>What a check trusts: the certificates trusted by the user and the own signers.</summary>
    public DescriptorTrust Trust()
    {
        var trusted = new List<X509Certificate2>();
        if (Directory.Exists(Trusted))
            foreach (var file in Directory.EnumerateFiles(Trusted, "*.der"))
            {
                try { trusted.Add(new X509Certificate2(File.ReadAllBytes(file))); }
                catch (System.Security.Cryptography.CryptographicException) { }
            }
        foreach (var own in OwnSigners())
        {
            using (own) trusted.Add(new X509Certificate2(own.Export(X509ContentType.Cert)));
        }
        return new DescriptorTrust(trusted);
    }
}
