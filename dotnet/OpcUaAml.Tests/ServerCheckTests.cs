using OpcUaAml.Addressing;
using OpcUaAml.Checks;
using OpcUaAml.Server;

namespace OpcUaAml.Tests;

/// <summary>
/// A running server checked against the types its instances claim: what an
/// engineer wants to know at commissioning, whether the PLC implements the
/// companion specification as declared. Each instance of the fixture breaks
/// one rule.
/// </summary>
[Trait("Speed", "Slow")]
public class ServerCheckTests : IAsyncLifetime
{
    private NodeSetServerHost _host = null!;
    private UaClient _client = null!;
    private ServerCheckResult _result = null!;

    public async Task InitializeAsync()
    {
        var pki = Path.Combine(Path.GetTempPath(), "amlopcua-test-pki-" + Guid.NewGuid().ToString("N")[..8]);
        _host = await NodeSetServerHost.StartAsync(new[] { Fixtures.Path("servercheck", "Check.NodeSet2.xml") },
            new NodeSetServerOptions { Port = Uaaml.FreePort(), PkiRoot = Path.Combine(pki, "server") });
        _client = await UaClient.ConnectAsync(new UaConnectOptions
        {
            EndpointUrl = _host.EndpointUrl, UseSecurity = false, AcceptUntrustedServerCertificates = true,
            PkiRoot = Path.Combine(pki, "client"),
        });
        _result = await ServerChecker.CheckAsync(_client);
    }

    public async Task DisposeAsync()
    {
        await _client.DisposeAsync();
        await _host.DisposeAsync();
    }

    private IReadOnlyList<Finding> At(string path) => _result.Findings.Where(f => f.ElementPath == path).ToList();

    [Fact]
    public void An_instance_with_everything_its_type_declares_has_no_finding() =>
        Assert.DoesNotContain(_result.Findings, f => f.ElementPath.StartsWith("Plant/GoodMachine", StringComparison.Ordinal));

    [Fact]
    public void A_missing_Mandatory_child_is_found()
    {
        var f = Assert.Single(At("Plant/NoSpeed"));
        Assert.Equal(Rules.ServerMissingMandatory, f.Rule);
        Assert.Contains("'Speed'", f.Message);
        Assert.Contains("MachineType", f.Message);
    }

    [Fact]
    public void A_Mandatory_child_declared_below_a_child_of_the_type_is_found_missing_there()
    {
        var f = Assert.Single(At("Plant/MotorWithoutCurrent/Motor"));
        Assert.Equal(Rules.ServerMissingMandatory, f.Rule);
        Assert.Contains("'Current'", f.Message);
    }

    [Fact]
    public void A_value_of_another_data_type_is_found() =>
        Assert.Equal(Rules.ServerWrongDataType, Assert.Single(At("Plant/TextSpeed/Speed")).Rule);

    [Fact]
    public void A_child_of_another_type_is_found()
    {
        var f = Assert.Single(At("Plant/WrongMotor/Motor"));
        Assert.Equal(Rules.ServerWrongTypeDefinition, f.Rule);
        Assert.Contains("ToolType", f.Message);
        Assert.Contains("MotorType", f.Message);
    }

    [Fact]
    public void A_MandatoryPlaceholder_without_any_child_is_found() =>
        Assert.Equal(Rules.ServerMissingMandatoryPlaceholder, Assert.Single(At("Plant/NoTool")).Rule);

    [Fact]
    public void An_instance_of_an_abstract_type_is_found() =>
        Assert.Equal(Rules.ServerAbstractType, Assert.Single(At("Plant/Abstract")).Rule);

    [Fact]
    public void A_subtype_adds_its_declarations_to_those_of_its_supertype()
    {
        var f = Assert.Single(At("Plant/Fast"));
        Assert.Equal(Rules.ServerMissingMandatory, f.Rule);
        Assert.Contains("'Boost'", f.Message);
    }

    [Fact]
    public void A_subtype_is_held_to_what_its_supertype_declares()
    {
        var f = Assert.Single(At("Plant/FastWithoutSpeed"));
        Assert.Equal(Rules.ServerMissingMandatory, f.Rule);
        Assert.Contains("'Speed'", f.Message);
        Assert.Contains("MachineType", f.Message);
    }

    [Fact]
    public void Every_finding_names_its_node_and_nothing_else_is_reported()
    {
        Assert.Equal(8, _result.Findings.Count);
        Assert.All(_result.Findings, f => Assert.StartsWith("nsu=http://example.org/Check/;", f.ElementId));
        Assert.False(_result.Truncated);
        Assert.True(_result.Checked >= 8, _result.Checked.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    [Fact]
    public async Task A_check_below_one_instance_checks_that_one_only()
    {
        var plant = (await _client.BrowseAsync()).Single(i => i.BrowseName == "Plant");
        var noSpeed = (await _client.BrowseAsync(plant.Address)).Single(i => i.BrowseName == "NoSpeed");

        var below = await ServerChecker.CheckAsync(_client, noSpeed.Address);

        Assert.Equal("NoSpeed", Assert.Single(below.Findings).ElementPath);
    }
}

/// <summary>The practice plant follows its model.</summary>
[Trait("Speed", "Slow")]
public class PracticePlantCheckTests
{
    [Fact]
    public async Task The_practice_plant_has_no_errors()
    {
        var pki = Path.Combine(Path.GetTempPath(), "amlopcua-test-pki-" + Guid.NewGuid().ToString("N")[..8]);
        await using var host = await NodeSetServerHost.StartAsync(new[] { Path.Combine(Repository.Root, "examples", "mps500", "MPS500.NodeSet2.xml") },
            new NodeSetServerOptions { Port = Uaaml.FreePort(), PkiRoot = Path.Combine(pki, "server") });
        await using var client = await UaClient.ConnectAsync(new UaConnectOptions
        {
            EndpointUrl = host.EndpointUrl, UseSecurity = false, AcceptUntrustedServerCertificates = true, PkiRoot = Path.Combine(pki, "client"),
        });

        var result = await ServerChecker.CheckAsync(client);

        Assert.True(result.Checked > 200, result.Checked.ToString(System.Globalization.CultureInfo.InvariantCulture));
        var errors = result.Findings.Where(f => f.Severity == Severity.Error).ToList();
        Assert.True(errors.Count == 0, string.Join("\n", errors));
    }
}
