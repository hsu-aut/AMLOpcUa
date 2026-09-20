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
        var builder = app.Build("urn:" + System.Net.Dns.GetHostName() + ":AMLOpcUa:" + applicationSuffix, "uri:hsu-aut:AMLOpcUa")
            .AsServer(new[] { url });
        var withPolicies = network
            ? builder.AddSignAndEncryptPolicies()
            : builder.AddUnsecurePolicyNone().AddSignAndEncryptPolicies();
        await withPolicies
            .AddSecurityConfiguration(subjectName, pkiRoot)
            .SetAutoAcceptUntrustedCertificates(!network)
            .CreateAsync(ct).ConfigureAwait(false);

        var replaced = false;
        try
        {
            await app.CheckApplicationInstanceCertificatesAsync(false, null, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The certificate names the addresses it was made for (the computer's name, or
            // 127.0.0.1); served under another, the stack refuses it. It is the server's own,
            // so a new one is made. Clients that trusted the old one are asked again.
            foreach (var folder in new[] { "certs", "private" }.Select(f => Path.Combine(pkiRoot, "own", f)).Where(Directory.Exists))
                foreach (var file in Directory.GetFiles(folder)) File.Delete(file);
            app.ApplicationConfiguration.SecurityConfiguration.ApplicationCertificate.Certificate = null;
            await app.CheckApplicationInstanceCertificatesAsync(false, null, ct).ConfigureAwait(false);
            replaced = true;
        }
        return (app, url, replaced);
    }
}
