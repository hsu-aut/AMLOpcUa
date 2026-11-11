using System.IO;
using Aml.Editor.Plugin.OpcUa;
using Aml.Editor.Plugin.OpcUa.Localization;
using Opc.Ua;
using OpcUaAml.Server;

namespace PluginTests;

/// <summary>
/// What the user reads when connecting did not work. Seen in the audit: a
/// trust dialog answered with Cancel was logged and told as an error, and a
/// server that refused this computer's certificate said nothing about which
/// certificate its administrator has to trust.
/// </summary>
public class ConnectFailureTests
{
    private const string Url = "opc.tcp://plc:4840";
    private static readonly string Pki = Path.Combine("C:", "pki");

    private static (string Text, bool IsError) Describe(Exception ex)
    {
        var german = L.German;
        L.German = false;
        try { return OpcUaPlugin.DescribeConnectFailure(ex, Url, Pki); }
        finally { L.German = german; }
    }

    [Fact]
    public void Declining_the_certificate_is_no_error()
    {
        var (text, isError) = Describe(new OpcUaPlugin.TrustDeclinedException());

        Assert.False(isError);
        Assert.Equal("Not connected: the certificate of opc.tcp://plc:4840 was not trusted.", text);
    }

    [Fact]
    public void A_server_that_refuses_this_computer_names_the_certificate_to_trust()
    {
        var refused = new UaConnectionException("Connecting to 'opc.tcp://plc:4840' failed: BadSecurityChecksFailed",
            new ServiceResultException(StatusCodes.BadSecurityChecksFailed));

        var (text, isError) = Describe(refused);

        Assert.True(isError);
        Assert.Contains(Path.Combine(Pki, "own", "certs"), text);
    }

    [Fact]
    public void Refused_again_after_trusting_the_server_names_the_certificate_to_trust()
    {
        var (text, isError) = Describe(new OpcUaPlugin.ClientRefusedException());

        Assert.True(isError);
        Assert.Contains(Path.Combine(Pki, "own", "certs"), text);
    }

    [Fact]
    public void Other_failures_keep_their_message()
    {
        var (text, isError) = Describe(new UaConnectionException("No endpoint at 'opc.tcp://plc:4840': timeout"));

        Assert.True(isError);
        Assert.Equal("No endpoint at 'opc.tcp://plc:4840': timeout", text);
    }

    [Fact]
    public void The_endpoint_offered_first_is_this_computer_by_its_address()
    {
        // "localhost" tries ::1 first; a server on 127.0.0.1 only then answers
        // after about two seconds, on every connection.
        Assert.StartsWith("opc.tcp://127.0.0.1:4840", OpcUaPlugin.DefaultEndpoint);
    }
}
