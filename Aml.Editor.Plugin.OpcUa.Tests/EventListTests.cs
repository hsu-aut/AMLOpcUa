using System.IO;
using System.Windows.Controls;
using Aml.Editor.Plugin.OpcUa;
using Aml.Editor.Plugin.OpcUa.Diagnostics;
using Aml.Editor.Plugin.OpcUa.Localization;
using Aml.Engine.CAEX;
using OpcUaAml.Addressing;
using OpcUaAml.Server;
using static PluginTests.LessonHarness;

namespace PluginTests;

/// <summary>
/// The events and alarms of the server, below its address space beside the
/// watched values: received as soon as the plugin connects (unless the
/// setting says otherwise), kept across connections, and bounded however
/// many a server sends.
/// </summary>
public class EventListTests
{
    private static readonly UaNodeAddress Spindle = new("urn:plant", UaIdType.String, "Drill/SpindleHigh");

    /// <summary>Runs <paramref name="body"/> with a plugin connected to the practice plant.</summary>
    private static void WithPlant(Func<OpcUaPlugin, string, Func<Task<UaClient>>, Task> body, SimulationPace? watchedPace = null)
    {
        ProvideConverter();
        var pki = Directory.CreateTempSubdirectory("opcuaaml-events-pki-").FullName;
        try
        {
            OnUiThread(async () =>
            {
                var plugin = new OpcUaPlugin();
                try
                {
                    plugin.DocumentLoaded(CAEXDocument.New_CAEXDocument(CAEXDocument.CAEXSchema.CAEX3_0));
                    await plugin.StartPracticePlantAsync(FreePort(), Path.Combine(pki, "server"), watchedPace);
                    var url = plugin.PracticePlant!.EndpointUrl;
                    Task<UaClient> Connect() => UaClient.ConnectAsync(new UaConnectOptions { EndpointUrl = url, UseSecurity = false, PkiRoot = Path.Combine(pki, "client") });
                    await body(plugin, url, Connect);
                }
                finally
                {
                    await plugin.StopPracticePlantAsync();
                }
            });
        }
        finally
        {
            try { Directory.Delete(pki, recursive: true); } catch (IOException) { }
        }
    }

    /// <summary>Saves the setting for the plugins created next, and puts it back afterwards.</summary>
    private static void WithReceiveEvents(bool on, Action test)
    {
        var settings = PluginSettings.Load(out _);
        var before = settings.ReceiveEvents;
        settings.ReceiveEvents = on;
        settings.Save();
        try { test(); }
        finally
        {
            settings.ReceiveEvents = before;
            settings.Save();
        }
    }

    private static UaEvent Alarm(int id, bool active, bool acked, ushort severity = 800) =>
        new(BitConverter.GetBytes(id), null, null, "Drill", DateTime.UtcNow, "Spindle overloaded", severity,
            Spindle, "SpindleHigh", active, acked, true, null, null);

    [Fact]
    public void Events_are_received_after_connecting_and_say_where_from()
    {
        bool receiving = false;
        IReadOnlyList<EventRow> rows = [];
        string header = "";
        WithReceiveEvents(true, () => WithPlant(async (plugin, url, connect) =>
        {
            await plugin.AttachClientAsync(await connect(), url);
            await Until(() => plugin.ReceivingEvents, 20);
            receiving = plugin.ReceivingEvents;
            await Until(() => plugin.EventList.Items.Count > 0, 5);
            rows = plugin.Events.Rows;
            header = plugin.EventsTab.Header?.ToString() ?? "";
        }));

        Assert.True(receiving);
        var connection = Assert.Single(rows, r => r.Event == null);
        Assert.Equal("AMLOpcUa NodeSet server", connection.Server);
        Assert.Contains($"({rows.Count})", header);
    }

    [Fact]
    public void The_alarms_of_the_practice_plant_reach_the_list()
    {
        int served = 0;
        string? message = null;
        WithReceiveEvents(true, () => WithPlant(async (plugin, url, connect) =>
        {
            served = plugin.PracticePlant!.Alarms;
            await plugin.AttachClientAsync(await connect(), url);
            await Until(() => plugin.Events.Rows.Any(r => r.Event?.ConditionName == "SpindleOverload"), 30);
            message = plugin.Events.Rows.FirstOrDefault(r => r.Event?.ConditionName == "SpindleOverload")?.Event?.Message;
        }, new SimulationPace(4, 1)));

        Assert.Equal(4, served);
        Assert.Equal("Drill spindle overloaded", message);
    }

    [Fact]
    public void With_the_setting_off_nothing_is_received_until_asked()
    {
        bool before = true, offered = false, after = false;
        WithReceiveEvents(false, () => WithPlant(async (plugin, url, connect) =>
        {
            await plugin.AttachClientAsync(await connect(), url);
            before = plugin.ReceivingEvents;
            offered = plugin.ReceiveEventsItem.IsEnabled && plugin.ReceiveEventsItem.Visibility == System.Windows.Visibility.Visible;
            Click(plugin.ReceiveEventsItem);
            await Until(() => plugin.ReceivingEvents, 20);
            after = plugin.ReceivingEvents;
        }));

        Assert.False(before);
        Assert.True(offered);
        Assert.True(after);
    }

    [Fact]
    public void The_list_stays_across_a_reconnect()
    {
        int connections = 0;
        WithReceiveEvents(true, () => WithPlant(async (plugin, url, connect) =>
        {
            await plugin.AttachClientAsync(await connect(), url);
            await plugin.DisconnectAsync();
            await plugin.AttachClientAsync(await connect(), url);
            connections = plugin.Events.Rows.Count(r => r.Event == null);
        }));

        Assert.Equal(2, connections);
    }

    [Fact]
    public void Events_arrive_in_batches_and_the_list_stays_bounded()
    {
        int shown = 0;
        string status = "";
        OnUiThread(async () =>
        {
            var plugin = new OpcUaPlugin();
            plugin.DeliverEvents(Enumerable.Range(1, 20_000).Select(i => Alarm(i, active: i % 2 == 0, acked: false, severity: 100)));
            await Until(() => plugin.EventList.Items.Count > 0, 5);
            shown = plugin.EventList.Items.Count;
            status = plugin.StatusText.Text;
        });

        Assert.InRange(shown, 1, 10_000);
        Assert.Contains("10000 oldest", status);
    }

    [Fact]
    public void Only_open_alarms_and_the_severity_narrow_the_list()
    {
        int all = 0, open = 0, severe = 0;
        OnUiThread(async () =>
        {
            var plugin = new OpcUaPlugin();
            plugin.DeliverEvents([Alarm(1, active: true, acked: false, severity: 800), Alarm(2, active: true, acked: true, severity: 800)]);
            plugin.DeliverEvents([Alarm(3, active: true, acked: false, severity: 200) with { ConditionId = new UaNodeAddress("urn:plant", UaIdType.String, "Load") }]);
            await Until(() => plugin.EventList.Items.Count == 3, 5);
            all = plugin.EventList.Items.Count;
            plugin.OpenOnlyBox.IsChecked = true;
            open = plugin.EventList.Items.Count;
            plugin.SeverityBox.SelectedIndex = 3;   // 800
            severe = plugin.EventList.Items.Count;
        });

        Assert.Equal(3, all);
        Assert.Equal(2, open);
        Assert.Equal(1, severe);
    }

    [Fact]
    public void State_text_is_translated()
    {
        var german = L.German;
        L.German = true;
        try
        {
            var row = new EventRow(DateTimeOffset.Now, "Plant", Alarm(1, active: true, acked: false));
            var view = new OpcUaPlugin.EventRowView(row);

            Assert.Equal("aktiv, nicht quittiert", view.State);
            Assert.True(view.Active);
            Assert.Equal("Verbunden mit Plant", new OpcUaPlugin.EventRowView(new EventRow(DateTimeOffset.Now, "Plant", null)).Message);
        }
        finally { L.German = german; }
    }
}
