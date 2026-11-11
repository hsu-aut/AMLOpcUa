using System.Security.Cryptography.X509Certificates;
using Aml.Engine.CAEX;
using Aml.Engine.CAEX.Extensions;
using Opc.Ua;
using Opc.Ua.Configuration;
using OpcUaAml.Addressing;
using OpcUaAml.Server;

namespace OpcUaAml.Tests;

/// <summary>
/// The ApplicationUris and certificates of this build's own servers and
/// client. A document mirrored on one computer has to name the same server on
/// the next, and certificates of earlier builds must not get in the way.
/// </summary>
[Trait("Speed", "Slow")]
public class OwnCertificateTests
{
    private const string DocumentNamespace = "urn:amlopcua:document";

    private static string TempPki() => Path.Combine(Path.GetTempPath(), "amlopcua-test-pki-" + Guid.NewGuid().ToString("N")[..8]);

    private static int FreePort() => NodeSetServerFixture.FreePort();

    private static CAEXDocument Booth()
    {
        var doc = CAEXDocument.New_CAEXDocument();
        var value = doc.CAEXFile.InstanceHierarchy.Append("Booth").InternalElement.Append("Speed").Attribute.Append("Value");
        value.AttributeDataType = "xs:double";
        value.Value = "1.5";
        return doc;
    }

    private static UaConnectOptions Options(string endpoint, string pki, bool secure = false) => new()
    {
        EndpointUrl = endpoint, UseSecurity = secure, AcceptUntrustedServerCertificates = true, PkiRoot = pki,
    };

    /// <summary>A certificate as builds up to 0.2.0 made it, in the folder a server or client of this build uses.</summary>
    private static async Task SeedAsync(string pki, string applicationUri, string subject, string? serverUrl = null, ushort? months = null)
    {
#pragma warning disable CS0618 // the constructor the code under test uses as well
        var app = new ApplicationInstance { ApplicationName = "earlier build", ApplicationType = serverUrl != null ? ApplicationType.Server : ApplicationType.Client };
#pragma warning restore CS0618
        var builder = app.Build(applicationUri, "uri:hsu-aut:AMLOpcUa");
        if (serverUrl != null)
            await builder.AsServer(new[] { serverUrl }).AddUnsecurePolicyNone().AddSignAndEncryptPolicies().AddSecurityConfiguration(subject, pki).CreateAsync();
        else
            await builder.AsClient().AddSecurityConfiguration(subject, pki).CreateAsync();
        await app.CheckApplicationInstanceCertificatesAsync(false, months);
    }

    private static X509Certificate2 Own(string pki) =>
        X509CertificateLoader.LoadCertificateFromFile(Directory.GetFiles(Path.Combine(pki, "own", "certs"), "*.der").Single());

    private static string? UriOf(X509Certificate2 certificate) => X509Utils.GetApplicationUriFromCertificate(certificate);

    private static IEnumerable<AttributeType> AllAttributes(IObjectWithAttributes owner) =>
        owner.Attribute.SelectMany(a => new[] { a }.Concat(AllAttributes(a)));

    [Fact]
    public async Task The_document_server_names_no_computer_in_its_ApplicationUri()
    {
        var pki = TempPki();
        await using var host = await AmlServerHost.StartAsync(Booth(), new AmlServerOptions { Port = FreePort(), PkiRoot = Path.Combine(pki, "server") });
        await using var client = await UaClient.ConnectAsync(Options(host.EndpointUrl, Path.Combine(pki, "client")));

        Assert.Equal("urn:AMLOpcUa:DocumentServer", client.ServerUri);
        Assert.Equal("urn:AMLOpcUa:DocumentServer", UriOf(Own(Path.Combine(pki, "server"))));
    }

    [Fact]
    public async Task A_server_certificate_that_names_a_computer_is_made_anew()
    {
        // Made by 0.2.0 on the office computer, the PKI folder then copied or the computer renamed.
        var pki = TempPki();
        var serverPki = Path.Combine(pki, "server");
        var port = FreePort();
        await SeedAsync(serverPki, "urn:OFFICE-PC:AMLOpcUa:DocumentServer", "CN=AMLOpcUa document server, O=AMLOpcUa", $"opc.tcp://127.0.0.1:{port}/AMLOpcUa");

        await using var host = await AmlServerHost.StartAsync(Booth(), new AmlServerOptions { Port = port, PkiRoot = serverPki });
        await using var client = await UaClient.ConnectAsync(Options(host.EndpointUrl, Path.Combine(pki, "client")));

        Assert.True(host.CertificateReplaced);
        Assert.Equal("urn:AMLOpcUa:DocumentServer", client.ServerUri);
        Assert.Equal("urn:AMLOpcUa:DocumentServer", UriOf(Own(serverPki)));
    }

    [Fact]
    public async Task A_document_mirrored_from_an_earlier_build_on_another_computer_names_this_server()
    {
        // Prepared in the office with 0.2.0 ("urn:OFFICE-PC:AMLOpcUa:DocumentServer"),
        // shown on the booth laptop against the same served document.
        var pki = TempPki();
        await using var host = await AmlServerHost.StartAsync(Booth(), new AmlServerOptions { Port = FreePort(), PkiRoot = Path.Combine(pki, "server") });
        await using var client = await UaClient.ConnectAsync(Options(host.EndpointUrl, Path.Combine(pki, "client")));
        var doc = CAEXDocument.New_CAEXDocument();
        var ih = doc.CAEXFile.InstanceHierarchy.Append("OpcUaServer");
        var selection = new MirrorSelection { Items = { new MirrorItem(new UaNodeAddress(DocumentNamespace, UaIdType.String, "Booth"), MirrorScope.Subtree) } };
        var first = await AddressSpaceMirror.MirrorSelectionAsync(client, selection, ih);
        const string office = "urn:OFFICE-PC:AMLOpcUa:DocumentServer";
        first.Server.Attribute[AddressSpaceMirror.ServerUriAttribute]!.Value = office;
        foreach (var uri in first.Server.Descendants<InternalElementType>().SelectMany(AllAttributes).Where(a => a.Name == "ServerInstanceUri"))
            uri.Value = office;
        var speed = first.Server.Descendants<InternalElementType>().Single(e => e.Name == "Speed");
        speed.Attribute["Value"]!.Value = "0";

        var found = AddressSpaceMirror.MirroredServer(ih, client);
        var snapshot = await ValueSnapshot.ApplyAsync(doc, client);
        var again = await AddressSpaceMirror.MirrorSelectionAsync(client, selection, ih);

        Assert.Equal(first.Server.ID, found?.ID);
        Assert.Equal(0, snapshot.Skipped);
        Assert.Equal("1.5", speed.Attribute["Value"]!.Value);
        Assert.Equal(first.Server.ID, again.Server.ID);
        Assert.Single(ih.InternalElement);
    }

    [Fact]
    public void Earlier_ApplicationUris_of_this_build_name_the_same_server()
    {
        Assert.True(OwnApplicationUris.Same("urn:OFFICE-PC:AMLOpcUa:NodeSetServer", OwnApplicationUris.NodeSetServer));
        Assert.True(OwnApplicationUris.Same("urn:OFFICE-PC:AMLOpcUa:DocumentServer", "urn:booth:AMLOpcUa:DocumentServer"));
        Assert.False(OwnApplicationUris.Same("urn:OFFICE-PC:AMLOpcUa:DocumentServer", OwnApplicationUris.NodeSetServer));
        Assert.False(OwnApplicationUris.Same("urn:plc-17:Siemens:S7-1500", "urn:plc-18:Siemens:S7-1500"));
        Assert.True(OwnApplicationUris.Same("urn:plc-17:Siemens:S7-1500", "urn:plc-17:Siemens:S7-1500"));
    }

    [Fact]
    public async Task The_client_names_no_computer_in_its_ApplicationUri()
    {
        var pki = TempPki();
        await using var host = await AmlServerHost.StartAsync(Booth(), new AmlServerOptions { Port = FreePort(), PkiRoot = Path.Combine(pki, "server") });

        await using var client = await UaClient.ConnectAsync(Options(host.EndpointUrl, Path.Combine(pki, "client"), secure: true));

        Assert.Equal("urn:AMLOpcUa:Client", UriOf(Own(Path.Combine(pki, "client"))));
    }

    [Fact]
    public async Task A_client_certificate_that_names_another_computer_still_connects_secured()
    {
        // The client's certificate of 0.2.0, made on another computer (the
        // folder copied, the computer renamed). Servers may trust exactly it.
        var pki = TempPki();
        var clientPki = Path.Combine(pki, "client");
        await SeedAsync(clientPki, "urn:OFFICE-PC:AMLOpcUa", "CN=AMLOpcUa, O=AMLOpcUa");
        var before = Own(clientPki).Thumbprint;
        await using var host = await AmlServerHost.StartAsync(Booth(), new AmlServerOptions { Port = FreePort(), PkiRoot = Path.Combine(pki, "server") });

        await using var client = await UaClient.ConnectAsync(Options(host.EndpointUrl, clientPki, secure: true));

        Assert.StartsWith("SignAndEncrypt", client.SecurityMode);
        Assert.Equal(before, Own(clientPki).Thumbprint);
    }
}
