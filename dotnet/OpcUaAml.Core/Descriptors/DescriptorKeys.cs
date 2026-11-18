// The keys the plugin signs Descriptors with and the certificates it trusts
// when it checks them (OPC 10000-83 8.3.4, 7.8.2), each in a folder of its
// own below %LOCALAPPDATA%\AMLOpcUa\descriptors:
//
//   signers\<thumbprint>.key    own certificates with their keys, a .pfx
//                               encrypted for the user's account (UserSecret)
//   trusted\<thumbprint>.der    certificates trusted by the user
//
// What one signs oneself is trusted. Whoever runs as the user can add to
// trusted\, as to the user's certificate store of Windows: trust is per user.

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
        foreach (var file in Directory.EnumerateFiles(Signers, "*.key"))
        {
            try { found.Add(new X509Certificate2(UserSecret.Read(file), (string?)null, X509KeyStorageFlags.EphemeralKeySet)); }
            catch (System.Security.Cryptography.CryptographicException) { }
        }
        return found.OrderByDescending(c => c.NotBefore).ToList();
    }

    /// <summary>Makes a self-signed certificate for signing (RSA 2048, three years) and keeps it with its key.</summary>
    public X509Certificate2 CreateSigner(string name)
    {
        var certificate = DescriptorCertificates.CreateSelfSigned(name, DateTimeOffset.Now);
        Directory.CreateDirectory(Signers);
        UserSecret.Write(Path.Combine(Signers, certificate.Thumbprint + ".key"), certificate.Export(X509ContentType.Pfx));
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
