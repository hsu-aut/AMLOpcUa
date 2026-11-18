using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using Aml.Editor.Plugin.OpcUa;
using Aml.Engine.CAEX;
using OpcUaAml.Addressing;
using OpcUaAml.Checks;
using OpcUaAml.Server;
using static PluginTests.LessonHarness;

namespace PluginTests;

/// <summary>Smaller findings of the review of 2026-11-18, each where a user meets it.</summary>
public class PluginDetailsTests
{
    // Stop watching cleared the recording, but values already on their way still went into it:
    // Save values… stayed enabled and saved values of the stopped watch.
    [Fact]
    public void A_value_for_a_row_no_longer_watched_is_not_recorded()
    {
        int recorded = -1;
        OnUiThread(() =>
        {
            var plugin = new OpcUaPlugin();
            var address = new UaNodeAddress("urn:plant", UaIdType.String, "Drill/Speed");
            var stopped = new OpcUaPlugin.WatchRow { Address = address, Node = "Speed" };

            plugin.RecordWatchValue(stopped, new UaReadResult(address, 1.5, "Double", true, "Good"));
            recorded = plugin.RecordedValues;
            return Task.CompletedTask;
        });

        Assert.Equal(0, recorded);
    }

    // Enter in the discovery server field pressed Use, the default button, and took the
    // server found before instead of searching the one just typed.
    [Fact]
    public void Enter_in_the_discovery_server_field_searches()
    {
        bool handled = false;
        string? chosen = "none yet";
        OnUiThread(async () =>
        {
            var window = new FindServersWindow("127.0.0.1:1") { ShowInTaskbar = false, Left = -4000 };
            window.Show();
            try
            {
                await Task.Delay(300);
                var box = Logical(window).OfType<TextBox>().First();
                var enter = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(box)!, 0, Key.Enter) { RoutedEvent = Keyboard.KeyDownEvent };
                box.RaiseEvent(enter);
                handled = enter.Handled;
                chosen = window.Chosen;
            }
            finally { window.Close(); }
        });

        Assert.True(handled);
        Assert.Null(chosen);
    }

    // A finding of Check server names a node of the server; Enter on it did nothing at all.
    [Fact]
    public void A_finding_about_no_element_of_the_document_says_so()
    {
        string status = "";
        OnUiThread(() =>
        {
            var plugin = new OpcUaPlugin();
            plugin.DocumentLoaded(CAEXDocument.New_CAEXDocument(CAEXDocument.CAEXSchema.CAEX3_0));
            var finding = new Finding("SV003", Severity.Error, "Machine/Speed", "Mandatory child Speed is missing.", "nsu=urn:plant;i=5");
            plugin.FindingList.ItemsSource = new[] { new FindingRow(finding) };
            plugin.FindingList.SelectedIndex = 0;

            plugin.SelectFinding();
            status = plugin.StatusText.Text;
            return Task.CompletedTask;
        });

        Assert.Contains("nsu=urn:plant;i=5", status);
    }

    // A screen reader heard nothing when Write was pressed with a value of the wrong type.
    [Fact]
    public void The_message_line_of_a_dialog_is_read_out_when_it_changes()
    {
        var setting = AutomationLiveSetting.Off;
        OnUiThread(() =>
        {
            var window = new WriteValueWindow("Speed", "nsu=urn:x;i=1", new UaVariableInfo("Double", "Double", -1, true), "1.5");
            setting = AutomationProperties.GetLiveSetting(window.Error);
            return Task.CompletedTask;
        });

        Assert.Equal(AutomationLiveSetting.Polite, setting);
    }

    // Tab and the reading order reached the magnifier before the endpoint field it belongs to.
    [Fact]
    public void The_endpoint_field_comes_before_Find_servers()
    {
        int endpoint = -1, find = -1;
        OnUiThread(() =>
        {
            var plugin = new OpcUaPlugin();
            var panel = (Panel)plugin.EndpointBox.Parent;
            (endpoint, find) = (panel.Children.IndexOf(plugin.EndpointBox), panel.Children.IndexOf(plugin.FindServersButton));
            return Task.CompletedTask;
        });

        Assert.True(find > endpoint, $"endpoint {endpoint}, Find servers {find}");
    }
}
