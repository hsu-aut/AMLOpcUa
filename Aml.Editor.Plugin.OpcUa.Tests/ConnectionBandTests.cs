using System.Windows;
using System.Windows.Controls;
using Aml.Editor.Plugin.OpcUa;
using Aml.Editor.Plugin.OpcUa.Localization;

namespace PluginTests;

/// <summary>
/// The band at the top of the Server tab: the state, the endpoint URL, user,
/// password, Secure, Connect. Seen in the editor with the plugin beside the
/// trees: user, password and Secure took the width, and the endpoint URL box
/// got none, so no address could be typed.
/// </summary>
public class ConnectionBandTests
{
    private const string LongState = "Connected  ·  signed and encrypted, Aes256_Sha256_RsaPss";

    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
        {
            if (child is T t) yield return t;
            foreach (var d in Descendants<T>(child)) yield return d;
        }
    }

    /// <summary>The width of the endpoint box, and the controls of the band that stand beyond its edge.</summary>
    private static (double Endpoint, List<string> Outside) Layout(double width, string state)
    {
        double endpoint = 0;
        var outside = new List<string>();
        Exception? failure = null;
        var german = L.German;
        L.German = false;
        try
        {
            var thread = new Thread(() =>
            {
                try
                {
                    var plugin = new OpcUaPlugin();
                    var window = new Window { Content = plugin, Width = width, Height = 800, ShowInTaskbar = false, Left = -4000 };
                    window.Show();
                    plugin.ServerTab.IsSelected = true;
                    plugin.ServerStateText.Text = state;
                    window.Dispatcher.Invoke(window.UpdateLayout, System.Windows.Threading.DispatcherPriority.Loaded);
                    window.Dispatcher.Invoke(window.UpdateLayout, System.Windows.Threading.DispatcherPriority.Loaded);
                    var band = plugin.ConnectionBand;
                    endpoint = plugin.EndpointBox.IsVisible ? plugin.EndpointBox.ActualWidth : 0;
                    foreach (var c in Descendants<Control>(band).Where(c => c.IsVisible))
                    {
                        var right = c.TranslatePoint(new Point(c.ActualWidth, 0), band).X;
                        if (right > band.ActualWidth + 0.5 || c.ActualWidth < 1)
                            outside.Add(c.Name is { Length: > 0 } id ? id : c.GetType().Name);
                    }
                    // A state longer than its room is shortened with "…", not cut off at the edge.
                    var text = plugin.ServerStateText;
                    var pill = plugin.ConnectionPill;
                    if (text.TranslatePoint(new Point(text.ActualWidth, 0), pill).X > pill.ActualWidth + 0.5)
                        outside.Add(text.Name);
                    window.Close();
                }
                catch (Exception ex) { failure = ex; }
            });
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            thread.Join();
        }
        finally { L.German = german; }
        if (failure != null) throw new Xunit.Sdk.XunitException(failure.ToString());
        return (endpoint, outside);
    }

    // Connected while serving the document, as seen in the editor: the state took the
    // whole line, and the endpoint box was gone again.
    private const string ServingState = LongState + "  ·  serving at opc.tcp://127.0.0.1:48400/AMLOpcUa";

    [Theory]
    [InlineData(700, "Not connected")]
    [InlineData(900, LongState)]
    [InlineData(1400, LongState)]
    [InlineData(900, ServingState)]
    [InlineData(1400, ServingState)]
    public void The_endpoint_box_has_room_and_nothing_is_cut_off(double width, string state)
    {
        var (endpoint, outside) = Layout(width, state);

        Assert.True(endpoint >= 200, $"the endpoint box is {endpoint:0} wide");
        Assert.Empty(outside);
    }
}
