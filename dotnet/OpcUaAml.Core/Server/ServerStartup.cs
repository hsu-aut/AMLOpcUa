// What both servers of this build need before they can listen: an application
// with its endpoints, its security policies and a certificate that names the
// address it is served at. The document server (AmlServerHost) and the NodeSet
// server (NodeSetServerHost) differ in their address space, not in this.

using Opc.Ua.Configuration;

namespace OpcUaAml.Server;

internal static class ServerStartup
{
    /// <summary>
    /// Prepares an OPC UA application for a server on this computer, or on the
    /// network. On the loopback address it offers the unsecured endpoint as
    /// well and admits every client, which is what testing here needs; on the
    /// network only secured endpoints and trusted clients.
    /// </summary>
    /// <returns>The application, the endpoint it listens at, and whether the certificate had to be made anew.</returns>
    public static async Task<(ApplicationInstance App, string Url, bool CertificateReplaced)> PrepareAsync(
        string applicationName, string applicationSuffix, string subjectName,
        int port, bool network, string pkiRoot, CancellationToken ct)
    {
        // The stack listens on every address for a host name, on that address alone for an IP address.
        var host = network ? System.Net.Dns.GetHostName() : "127.0.0.1";
        var url = $"opc.tcp://{host}:{port}/AMLOpcUa";

        var app = new ApplicationInstance { ApplicationName = applicationName, ApplicationType = Opc.Ua.ApplicationType.Server };
        // No computer's name in it: a document mirrored here names the same server
        // on the next computer (OwnApplicationUris).
        var builder = app.Build("urn:AMLOpcUa:" + applicationSuffix, "uri:hsu-aut:AMLOpcUa")
            .AsServer(new[] { url });
        var withPolicies = network
            ? builder.AddSignAndEncryptPolicies()
            : builder.AddUnsecurePolicyNone().AddSignAndEncryptPolicies();
        await withPolicies
            .AddSecurityConfiguration(subjectName, pkiRoot)
            .SetAutoAcceptUntrustedCertificates(!network)
            .CreateAsync(ct).ConfigureAwait(false);
        // While sessions exist the stack waits this many seconds before it stops, on
        // the thread that stops it (the editor's UI thread). Clients notice a stopped
        // server through their keep-alive anyway.
        app.ApplicationConfiguration.ServerConfiguration.ShutdownDelay = 0;

        // The certificate names the addresses it was made for (the computer's name, or
        // 127.0.0.1); served under another, the stack refuses it. It is the server's
        // own, so a new one is made then; clients that trusted the old one are asked again.
        var replaced = await OwnCertificate.EnsureAsync(app, subjectName, pkiRoot, ct).ConfigureAwait(false);
        return (app, url, replaced);
    }

    /// <summary>
    /// Starts the server. A port another program holds is said in a sentence
    /// (<see cref="PortInUseException"/>), not in the stack's "Failed to
    /// establish tcp listener sockets".
    /// </summary>
    public static async Task StartAsync(ApplicationInstance app, Opc.Ua.ServerBase server, int port)
    {
        try
        {
            await app.StartAsync(server).ConfigureAwait(false);
        }
        catch (Exception ex) when (HeldByAnother(ex))
        {
            throw new PortInUseException(port, ex);
        }
    }

    private static bool HeldByAnother(Exception ex)
    {
        for (Exception? e = ex; e != null; e = e.InnerException)
            if (e is System.Net.Sockets.SocketException { SocketErrorCode: System.Net.Sockets.SocketError.AddressAlreadyInUse }) return true;
        return false;
    }
}

/// <summary>The certificate of one of this build's applications, kept usable: made anew when it is not.</summary>
internal static class OwnCertificate
{
    /// <summary>
    /// Checks the application's own certificate and makes a new one when the
    /// stack refuses it or it does not fit (<see cref="FitsAsync"/>).
    /// </summary>
    /// <returns>Whether a new certificate was made.</returns>
    public static async Task<bool> EnsureAsync(ApplicationInstance app, string subjectName, string pkiRoot, CancellationToken ct,
        string? applicationUriForNew = null)
    {
        bool usable;
        try
        {
            await app.CheckApplicationInstanceCertificatesAsync(false, null, ct).ConfigureAwait(false);
            usable = await FitsAsync(app, subjectName).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The stack refuses a certificate made for other addresses or another ApplicationUri.
            usable = false;
        }
        if (usable) return false;

        foreach (var folder in new[] { "certs", "private" }.Select(f => Path.Combine(pkiRoot, "own", f)).Where(Directory.Exists))
            foreach (var file in Directory.GetFiles(folder)) File.Delete(file);
        if (applicationUriForNew != null) app.ApplicationConfiguration.ApplicationUri = applicationUriForNew;
        app.ApplicationConfiguration.SecurityConfiguration.ApplicationCertificate.Certificate = null;
        await app.CheckApplicationInstanceCertificatesAsync(false, null, ct).ConfigureAwait(false);
        return true;
    }

    /// <summary>
    /// How long before it expires a certificate is made anew. The stack makes
    /// them for twelve months and never renews them; an expired one is refused
    /// by every peer that checks, and a renewed one has to be trusted again.
    /// </summary>
    public static readonly TimeSpan RenewBefore = TimeSpan.FromDays(30);

    /// <summary>
    /// Whether the certificate the stack found has the subject and the
    /// ApplicationUri asked for, and is valid for a while yet (<see cref="RenewBefore"/>).
    /// The stack takes one whose common name matches,
    /// though the rest of the subject differs: before 0.2.0 the subject had no
    /// organisation and the stack filled in "O=OPC Foundation", which a client
    /// then showed when it asked to trust it.
    /// </summary>
    private static async Task<bool> FitsAsync(ApplicationInstance app, string subjectName)
    {
        var certificate = await app.ApplicationConfiguration.SecurityConfiguration.ApplicationCertificate.FindAsync(false).ConfigureAwait(false);
        return certificate != null
               && Opc.Ua.X509Utils.CompareDistinguishedName(certificate.Subject, subjectName)
               && Opc.Ua.X509Utils.GetApplicationUrisFromCertificate(certificate).Contains(app.ApplicationConfiguration.ApplicationUri)
               && certificate.NotAfter.ToUniversalTime() > DateTime.UtcNow + RenewBefore;
    }
}

/// <summary>
/// A server of this build could not listen because another program holds its
/// port: another OPC UA server, a second <c>uaaml serve</c>, or the editor
/// serving a document.
/// </summary>
public sealed class PortInUseException : IOException
{
    public PortInUseException(int port, Exception? inner = null)
        : base($"Port {port} is in use (another server, a second serve or editor). Stop it or choose another port.", inner)
    {
        Port = port;
    }

    public int Port { get; }
}
