using System.IO;
using System.Windows.Controls;
using Aml.Editor.Plugin.OpcUa;
using Aml.Editor.Plugin.OpcUa.Localization;
using Aml.Engine.CAEX;
using OpcUaAml.Server;
using static PluginTests.LessonHarness;

namespace PluginTests;

/// <summary>
/// A variable whose server states its unit (EngineeringUnits) shows it in the
/// Node details; one without shows no unit.
/// </summary>
public class NodeDetailsUnitTests
{
    /// <summary>The text of a row of the details, or null where there is no such row.</summary>
    private static string? Row(OpcUaPlugin plugin, string label)
    {
        var name = plugin.NodeDetails.Children.OfType<TextBlock>().FirstOrDefault(t => t.Text == label);
        if (name == null) return null;
        var row = Grid.GetRow(name);
        var cell = plugin.NodeDetails.Children.OfType<DockPanel>().FirstOrDefault(c => Grid.GetRow(c) == row);
        return cell?.Children.OfType<TextBlock>().LastOrDefault()?.Text;
    }

    [Fact]
    public void The_node_details_show_the_unit_a_server_states()
    {
        var doc = CAEXDocument.New_CAEXDocument(CAEXDocument.CAEXSchema.CAEX3_0);
        var drill = doc.CAEXFile.InstanceHierarchy.Append("Plant").InternalElement.Append("Drill");
        var speed = drill.InternalElement.Append("SpindleSpeed").Attribute.Append("Value");
        speed.AttributeDataType = "xs:double";
        speed.Value = "1450";
        speed.Unit = "1/min";
        var holes = drill.InternalElement.Append("Holes").Attribute.Append("Value");
        holes.AttributeDataType = "xs:int";
        holes.Value = "3";
        var pki = Directory.CreateTempSubdirectory("opcuaaml-unit-pki-").FullName;
        string? withUnit = null, withoutUnit = "not read";
        try
        {
            OnUiThread(async () =>
            {
                await using var host = await AmlServerHost.StartAsync(doc, new AmlServerOptions { Port = FreePort(), PkiRoot = Path.Combine(pki, "server") });
                var plugin = new OpcUaPlugin();
                plugin.DocumentLoaded(CAEXDocument.New_CAEXDocument(CAEXDocument.CAEXSchema.CAEX3_0));
                var client = await UaClient.ConnectAsync(new UaConnectOptions
                {
                    EndpointUrl = host.EndpointUrl, UseSecurity = false, AcceptUntrustedServerCertificates = true, PkiRoot = Path.Combine(pki, "client"),
                });
                await plugin.AttachClientAsync(client, host.EndpointUrl);
                await plugin.LoadRootAsync();
                var plant = plugin.AddressTree.Items.OfType<TreeViewItem>().First(i => i.Tag is UaBrowseItem { DisplayName: "Plant" });
                var drillItem = await Child(plant, "Drill");

                (await Child(drillItem, "SpindleSpeed")).IsSelected = true;
                await Until(() => Row(plugin, L.T("Unit")) != null, 10);
                withUnit = Row(plugin, L.T("Unit"));

                (await Child(drillItem, "Holes")).IsSelected = true;
                await Until(() => Row(plugin, L.T("DataType")) != null, 10);
                await Task.Delay(300);
                withoutUnit = Row(plugin, L.T("Unit"));
                await client.DisposeAsync();
            });
        }
        finally
        {
            try { Directory.Delete(pki, recursive: true); } catch (IOException) { }
        }

        Assert.Equal("min⁻¹", withUnit);
        Assert.Null(withoutUnit);
    }
}
