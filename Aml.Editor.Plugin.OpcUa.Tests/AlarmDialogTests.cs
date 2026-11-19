using System.Windows.Controls;
using Aml.Editor.Plugin.OpcUa;
using Aml.Engine.CAEX;
using OpcUaAml.Alarms;
using OpcUaAml.Server;
using static PluginTests.LessonHarness;

namespace PluginTests;

/// <summary>
/// Create alarm…: for the value selected in the editor, grey with the reason
/// otherwise; the dialog offers what fits the value, proposes a name and a
/// text, and refuses a limit or a severity that is none.
/// </summary>
public class AlarmDialogTests
{
    /// <summary>A CAEX 3.0 document with a spindle speed (a number) and a magazine sensor (a boolean).</summary>
    private static (CAEXDocument Document, InternalElementType Speed, InternalElementType Empty, InternalElementType Drill) Plant()
    {
        var doc = CAEXDocument.New_CAEXDocument(CAEXDocument.CAEXSchema.CAEX3_0);
        var drill = doc.CAEXFile.InstanceHierarchy.Append("Plant").InternalElement.Append("Drill");
        var speed = drill.InternalElement.Append("SpindleSpeed");
        var v = speed.Attribute.Append("Value");
        v.AttributeDataType = "xs:double";
        v.Value = "1080";
        var empty = drill.InternalElement.Append("MagazineEmpty");
        var e = empty.Attribute.Append("Value");
        e.AttributeDataType = "xs:boolean";
        e.Value = "false";
        return (doc, speed, empty, drill);
    }

    /// <summary>What a message line shows: its runs, as a problem is shown with a glyph before it.</summary>
    private static string Shown(TextBlock block) =>
        string.Concat(block.Inlines.OfType<System.Windows.Documents.Run>().Select(r => r.Text));

    [Fact]
    public void The_button_says_why_it_is_grey()
    {
        bool none = true, drill = true, value = false;
        string? why = null;
        OnUiThread(() =>
        {
            var (doc, speed, _, element) = Plant();
            var plugin = new OpcUaPlugin();
            plugin.DocumentLoaded(doc);
            none = plugin.AlarmButton.IsEnabled;
            why = plugin.AlarmButton.ToolTip as string;
            plugin.ChangeSelectedObject(element);
            drill = plugin.AlarmButton.IsEnabled;
            plugin.ChangeSelectedObject(speed);
            value = plugin.AlarmButton.IsEnabled;
            return Task.CompletedTask;
        });

        Assert.False(none);
        Assert.Equal("Select a value in the editor first: an element with a Value.", why);
        Assert.False(drill);
        Assert.True(value);
    }

    [Fact]
    public void The_dialog_offers_what_fits_the_value()
    {
        bool highOnBoolean = true, stateOnBoolean = false, stateOnNumber = true;
        string name = "", message = "", severity = "", refused = "";
        AlarmRequest? request = null;
        OnUiThread(() =>
        {
            var (_, speed, empty, _) = Plant();
            var forBoolean = new AlarmWindow(empty);
            highOnBoolean = forBoolean.KindHigh.IsEnabled;
            stateOnBoolean = forBoolean.KindState.IsChecked == true;

            var window = new AlarmWindow(speed);
            stateOnNumber = window.KindState.IsEnabled;
            name = window.NameBox.Text;
            message = window.MessageBox.Text;
            severity = (window.SeverityBox.SelectedItem as ComboBoxItem)?.Content as string ?? "";
            window.LimitBox.Text = "abc";
            if (window.Accept() == null) refused = Shown(window.Error);
            window.LimitBox.Text = "1200";
            request = window.Accept();
            return Task.CompletedTask;
        });

        Assert.False(highOnBoolean);
        Assert.True(stateOnBoolean);
        Assert.False(stateOnNumber);
        Assert.Equal("SpindleSpeedHigh", name);
        Assert.Equal("SpindleSpeed too high", message);
        Assert.Equal("Fault (800)", severity);
        Assert.EndsWith("'abc' is not a number: write 1.5, for instance.", refused);
        Assert.Equal(new AlarmRequest(AlarmKind.High, 1200, null, 800, "SpindleSpeed too high", "SpindleSpeedHigh"), request);
    }

    [Fact]
    public void The_proposals_follow_the_kind_until_the_user_writes_their_own()
    {
        string lowName = "", lowMessage = "", kept = "";
        OnUiThread(() =>
        {
            var (_, speed, _, _) = Plant();
            var window = new AlarmWindow(speed);
            window.KindLow.IsChecked = true;
            lowName = window.NameBox.Text;
            lowMessage = window.MessageBox.Text;
            window.NameBox.Text = "SlowSpindle";
            window.KindHigh.IsChecked = true;
            kept = window.NameBox.Text;
            return Task.CompletedTask;
        });

        Assert.Equal("SpindleSpeedLow", lowName);
        Assert.Equal("SpindleSpeed too low", lowMessage);
        Assert.Equal("SlowSpindle", kept);
    }

    [Fact]
    public void A_custom_severity_is_1_to_1000()
    {
        string refused = "";
        AlarmRequest? accepted = null;
        OnUiThread(() =>
        {
            var (_, _, empty, _) = Plant();
            var window = new AlarmWindow(empty);
            window.SeverityBox.SelectedIndex = 3;
            window.CustomSeverityBox.Text = "1001";
            if (window.Accept() == null) refused = Shown(window.Error);
            window.CustomSeverityBox.Text = "1000";
            window.NormalTrue.IsChecked = true;
            accepted = window.Accept();
            return Task.CompletedTask;
        });

        Assert.EndsWith("A severity is 1 to 1000.", refused);
        Assert.Equal(1000, accepted!.Severity);
        Assert.True(accepted.Normal);
        Assert.Equal(AlarmKind.State, accepted.Kind);
    }

    [Fact]
    public void Without_the_base_library_the_plugin_asks_before_it_imports()
    {
        bool declined = true, asked = false;
        string status = "";
        OnUiThread(async () =>
        {
            var (doc, speed, _, _) = Plant();
            var plugin = new OpcUaPlugin();
            plugin.DocumentLoaded(doc);
            plugin.AskImportBaseLibrary = () => { asked = true; return false; };
            declined = await plugin.EnsureBaseLibraryAsync(doc);
            plugin.CreateAlarm(speed, new AlarmRequest(AlarmKind.High, 1200, null, 800, "x", "SpindleSpeedHigh"));
            status = plugin.StatusText.Text;
        });

        Assert.True(asked);
        Assert.False(declined);
        Assert.Equal("The document has no OPC UA base library.", status);
    }
}
