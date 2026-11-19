using System.IO;
using System.Windows.Controls;
using Aml.Editor.Plugin.OpcUa;
using Aml.Editor.Plugin.OpcUa.Diagnostics;
using Aml.Engine.CAEX;
using OpcUaAml.Addressing;
using OpcUaAml.Server;
using static PluginTests.LessonHarness;

namespace PluginTests;

/// <summary>
/// Acknowledge…: offered only for the newest state of an alarm that is not
/// acknowledged, asks first like Write value…, takes a comment if one is
/// given, and says what the server answered.
/// </summary>
public class AcknowledgeDialogTests
{
    private static readonly UaNodeAddress Spindle = new("urn:plant", UaIdType.String, "Drill/SpindleHigh");

    private static EventRow Row(bool active = true, bool acked = false) =>
        new(DateTimeOffset.Now, "Plant", new UaEvent([1, 2], null, null, "Drill", new DateTime(2026, 11, 19, 9, 41, 12, DateTimeKind.Utc),
            "Spindle overloaded", 800, Spindle, "SpindleHigh", active, acked, true, null, null));

    [Fact]
    public void The_dialog_names_the_alarm_and_escape_cancels()
    {
        string[] texts = [];
        bool cancels = false, accepted = false;
        OnUiThread(() =>
        {
            var window = new AcknowledgeWindow(Row(), null);
            // The facts are text boxes, to copy from; the rest text blocks.
            texts = Logical(window).OfType<TextBlock>().Select(t => t.Text)
                .Concat(Logical(window).OfType<TextBox>().Select(t => t.Text)).ToArray();
            cancels = Logical(window).OfType<Button>().Any(b => b.IsCancel);
            accepted = window.Accept();
            return Task.CompletedTask;
        });

        Assert.Contains("Spindle overloaded", texts);
        Assert.Contains("Drill", texts);
        Assert.Contains(texts, t => t.Contains("severity 800"));
        Assert.Contains(texts, t => t.Contains("Acknowledge only when you know the cause."));
        Assert.True(cancels);
        Assert.True(accepted);
    }

    /// <summary>Review 2026-11-19: "Since" was the time of the newest row, not when the alarm came.</summary>
    [Fact]
    public void The_dialog_says_since_when_an_active_alarm_is_active_and_when_another_changed()
    {
        string[] active = [], gone = [];
        var since = new DateTimeOffset(2026, 11, 19, 8, 30, 5, TimeSpan.Zero);
        OnUiThread(() =>
        {
            static string[] Texts(System.Windows.Window w) => Logical(w).OfType<TextBlock>().Select(t => t.Text)
                .Concat(Logical(w).OfType<TextBox>().Select(t => t.Text)).ToArray();
            active = Texts(new AcknowledgeWindow(Row(), since));
            gone = Texts(new AcknowledgeWindow(Row(active: false), null));
            return Task.CompletedTask;
        });

        Assert.Contains("Since", active);
        Assert.Contains(active, t => t.StartsWith(since.ToLocalTime().ToString("HH:mm:ss"), StringComparison.Ordinal));
        Assert.Contains("Last change", gone);
    }

    [Theory]
    [InlineData(true, "Good", "Acknowledged 'Spindle overloaded'.")]
    [InlineData(false, "BadEventIdUnknown", "The alarm has changed; select it again.")]
    [InlineData(false, "BadConditionBranchAlreadyAcked", "Acknowledging failed: BadConditionBranchAlreadyAcked")]
    public void The_status_says_what_the_server_answered(bool good, string status, string expected)
    {
        string text = "";
        OnUiThread(() =>
        {
            text = OpcUaPlugin.AcknowledgeText(new UaWriteResult(good, status), Row().Event!);
            return Task.CompletedTask;
        });

        Assert.Equal(expected, text);
    }

    /// <summary>
    /// Review 2026-11-19: the plugin acknowledged with the EventId of the newest
    /// state when the dialog closed, which may be a state the dialog never showed.
    /// </summary>
    [Fact]
    public void A_state_overtaken_while_the_dialog_was_open_is_not_acknowledged()
    {
        string status = "";
        OnUiThread(async () =>
        {
            var plugin = new OpcUaPlugin();
            var shown = Row();
            plugin.DeliverEvents([shown.Event!, shown.Event! with { EventId = [9, 9], Time = shown.Event!.Time.AddSeconds(5) }]);
            await Until(() => plugin.Events.Count == 2, 5);
            var older = plugin.Events.Rows.Last();

            await plugin.AcknowledgeRowAsync(older, "checked");
            status = plugin.StatusText.Text;
        });

        Assert.Equal("The alarm has changed; select it again.", status);
    }

    [Fact]
    public void Acknowledge_is_only_offered_for_the_newest_state_and_acknowledges_it()
    {
        bool forConnection = true, forLatest = false, acknowledged = false;
        string status = "";
        ProvideConverter();
        var pki = Directory.CreateTempSubdirectory("opcuaaml-ack-pki-").FullName;
        var settings = PluginSettings.Load(out _);
        var before = settings.ReceiveEvents;
        settings.ReceiveEvents = true;
        settings.Save();
        try
        {
            OnUiThread(async () =>
            {
                var plugin = new OpcUaPlugin();
                try
                {
                    plugin.DocumentLoaded(CAEXDocument.New_CAEXDocument(CAEXDocument.CAEXSchema.CAEX3_0));
                    await plugin.StartPracticePlantAsync(FreePort(), Path.Combine(pki, "server"), new SimulationPace(20, 10));
                    var url = plugin.PracticePlant!.EndpointUrl;
                    await plugin.AttachClientAsync(await UaClient.ConnectAsync(new UaConnectOptions { EndpointUrl = url, UseSecurity = false, PkiRoot = Path.Combine(pki, "client") }), url);
                    await Until(() => plugin.Events.Rows.Any(r => r.Event is { ConditionId: not null, Active: true, Acked: false }), 40);

                    plugin.EventList.SelectedItem = plugin.EventList.Items.OfType<OpcUaPlugin.EventRowView>().First(v => v.Row.Event == null);
                    forConnection = plugin.AcknowledgeButton.IsEnabled;
                    var open = plugin.Events.Rows.First(r => r.Event is { ConditionId: not null, Acked: false } && plugin.Events.CanAcknowledge(r));
                    plugin.EventList.SelectedItem = plugin.EventList.Items.OfType<OpcUaPlugin.EventRowView>().First(v => ReferenceEquals(v.Row, open));
                    forLatest = plugin.AcknowledgeButton.IsEnabled;

                    await plugin.AcknowledgeRowAsync(open, "checked");
                    // Read at once: the next alarm that comes is told in the same line.
                    status = plugin.StatusText.Text;
                    var condition = open.Event!.ConditionId!;
                    await Until(() => plugin.Events.LatestOf(condition)?.Event?.Acked == true, 10);
                    acknowledged = plugin.Events.LatestOf(condition)?.Event?.Acked == true;
                }
                finally
                {
                    await plugin.StopPracticePlantAsync();
                }
            });
        }
        finally
        {
            settings.ReceiveEvents = before;
            settings.Save();
            try { Directory.Delete(pki, recursive: true); } catch (IOException) { }
        }

        Assert.False(forConnection);
        Assert.True(forLatest);
        Assert.True(acknowledged);
        Assert.StartsWith("Acknowledged", status);
    }
}
