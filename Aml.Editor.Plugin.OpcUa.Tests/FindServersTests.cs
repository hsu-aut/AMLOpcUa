using Aml.Editor.Plugin.OpcUa;
using OpcUaAml.Server;

namespace PluginTests;

/// <summary>Find servers…: what Use takes into the endpoint box, and what a screen reader hears of a row.</summary>
public class FindServersTests
{
    private static readonly DiscoveredServer Plant = new("Plant", "urn:plant", "Server", ["opc.tcp://plc:4840"]);

    [Fact]
    public void An_endpoint_chosen_is_taken_with_its_security()
    {
        Assert.Equal(("opc.tcp://plc:4840/secure", (bool?)true),
            FindServersWindow.Choice(Plant, new DiscoveredEndpoint("opc.tcp://plc:4840/secure", "SignAndEncrypt", "Basic256Sha256", ["Anonymous"])));
        Assert.Equal(("opc.tcp://plc:4840", (bool?)false),
            FindServersWindow.Choice(Plant, new DiscoveredEndpoint("opc.tcp://plc:4840", "None", "None", ["Anonymous"])));
    }

    [Fact]
    public void A_server_chosen_alone_is_taken_by_its_discovery_url_and_leaves_security_as_it_was() =>
        Assert.Equal(("opc.tcp://plc:4840", (bool?)null), FindServersWindow.Choice(Plant, null));

    [Fact]
    public void Nothing_chosen_takes_nothing() =>
        Assert.Equal(((string?)null, (bool?)null), FindServersWindow.Choice(null, null));

    [Fact]
    public void A_row_says_what_it_shows()
    {
        Assert.Equal("Plant, opc.tcp://plc:4840", new ServerRow(Plant).ToString());
        Assert.Equal("opc.tcp://plc:4840, SignAndEncrypt Basic256Sha256, Anonymous, UserName",
            new EndpointRow(new DiscoveredEndpoint("opc.tcp://plc:4840", "SignAndEncrypt", "Basic256Sha256", ["Anonymous", "UserName"])).ToString());
    }
}
