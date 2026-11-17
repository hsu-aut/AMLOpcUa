using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using Aml.Editor.Plugin.OpcUa;

namespace PluginTests;

/// <summary>
/// The filters above the address space (Leave out properties, Objects only,
/// Show Server object) reloaded it on a click only. Ticked through UI
/// Automation, as a screen reader or a test does, the box changed and the
/// address space stayed as it was (seen 2026-11-17: the Server object did not
/// appear).
/// </summary>
public class FilterToggleTests
{
    private static int ItemsAfterToggling(Func<OpcUaPlugin, CheckBox> box)
    {
        var left = -1;
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var plugin = new OpcUaPlugin();
                var window = new Window { Content = plugin, Width = 1200, Height = 800, ShowInTaskbar = false, Left = -4000 };
                window.Show();
                var target = box(plugin);
                target.IsEnabled = true;
                // A tree from before, which a reload clears (without a server it stays empty).
                plugin.AddressTree.Items.Add(new TreeViewItem { Header = "from before" });
                ((IToggleProvider)UIElementAutomationPeer.CreatePeerForElement(target)).Toggle();
                left = plugin.AddressTree.Items.Count;
                window.Close();
            }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure != null) throw new Xunit.Sdk.XunitException(failure.ToString());
        return left;
    }

    [Fact]
    public void Show_Server_object_ticked_through_UI_Automation_reloads_the_address_space() =>
        Assert.Equal(0, ItemsAfterToggling(p => p.ShowServerBox));

    [Fact]
    public void Leave_out_properties_ticked_through_UI_Automation_reloads_the_address_space() =>
        Assert.Equal(0, ItemsAfterToggling(p => p.SkipPropertiesBox));

    [Fact]
    public void Objects_only_ticked_through_UI_Automation_reloads_the_address_space() =>
        Assert.Equal(0, ItemsAfterToggling(p => p.ObjectsOnlyBox));
}
