// The ApplicationUris of this build's own servers and client. Up to 0.2.0
// they named the computer ("urn:<host>:AMLOpcUa:DocumentServer"), and a
// mirrored document keeps the server's ApplicationUri (ServerUri, and the
// ServerInstanceUri of every NodeId): mirrored in the office, the same served
// document was another server on the booth laptop. Since 0.2.1 they name no
// computer, and the earlier ones still name the same server.

using System.Security.Cryptography.X509Certificates;

namespace OpcUaAml.Server;

public static class OwnApplicationUris
{
    /// <summary>The document server (<see cref="AmlServerHost"/>).</summary>
    public const string DocumentServer = "urn:AMLOpcUa:DocumentServer";

    /// <summary>The NodeSet server (<see cref="NodeSetServerHost"/>).</summary>
    public const string NodeSetServer = "urn:AMLOpcUa:NodeSetServer";

    /// <summary>The client (<see cref="UaClient"/>), for certificates made from 0.2.1 on.</summary>
    public const string Client = "urn:AMLOpcUa:Client";

    private const string Product = "AMLOpcUa";

    /// <summary>
    /// The ApplicationUri without the computer an earlier build named in it:
    /// "urn:OFFICE-PC:AMLOpcUa:DocumentServer" becomes <see cref="DocumentServer"/>.
    /// Any other URI stays as it is.
    /// </summary>
    public static string? Normalize(string? uri)
    {
        if (uri == null) return null;
        var parts = uri.Split(':');
        return parts.Length == 4 && parts[0] == "urn" && parts[1].Length > 0 && parts[2] == Product && parts[3].Length > 0
            ? $"urn:{Product}:{parts[3]}"
            : uri;
    }

    /// <summary>
    /// Whether two ApplicationUris name the same server: equal, or the same
    /// server of this build once with and once without a computer's name.
    /// </summary>
    public static bool Same(string? a, string? b) => string.Equals(Normalize(a), Normalize(b), StringComparison.Ordinal);

    /// <summary>The client's ApplicationUri up to 0.2.0: "urn:&lt;host&gt;:AMLOpcUa".</summary>
    private static bool IsEarlierClient(string? uri) =>
        uri?.Split(':') is { Length: 3 } parts && parts[0] == "urn" && parts[1].Length > 0 && parts[2] == Product;

    /// <summary>
    /// The ApplicationUri of the client certificate kept in <paramref name="pkiRoot"/>
    /// when it is this build's (<see cref="Client"/>, or an earlier build's with a
    /// computer's name): the client goes on with it, so servers that trust exactly
    /// that certificate keep doing so. Null when there is none.
    /// </summary>
    internal static string? KeptClientUri(string pkiRoot, string subjectName)
    {
        var folder = Path.Combine(pkiRoot, "own", "certs");
        if (!Directory.Exists(folder)) return null;
        string? uri = null;
        var notAfter = DateTime.MinValue;
        foreach (var file in Directory.EnumerateFiles(folder, "*.der"))
        {
            try
            {
                using var certificate = X509CertificateLoader.LoadCertificateFromFile(file);
                if (!Opc.Ua.X509Utils.CompareDistinguishedName(certificate.Subject, subjectName)) continue;
                var own = Opc.Ua.X509Utils.GetApplicationUriFromCertificate(certificate);
                if ((own != Client && !IsEarlierClient(own)) || certificate.NotAfter <= notAfter) continue;
                uri = own;
                notAfter = certificate.NotAfter;
            }
            catch (System.Security.Cryptography.CryptographicException) { /* not a certificate */ }
        }
        return uri;
    }
}
