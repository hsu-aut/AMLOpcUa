// The keys of the modeler stay in the modeler. WebView2 for WPF hands every
// key with Ctrl or Alt and every key without a character (Delete, arrows,
// Escape) to WPF as PreviewKeyDown and raises a KeyDown from it that bubbles
// to the editor's window, where Ctrl+Z, Ctrl+S, Ctrl+C, Delete and the like
// are bound. Ctrl+Z in the modeler then undid the last change of the AML
// document. The tests raise the event WebView2 raises (its internal
// WebView2KeyEventArgs) at a view inside a window that binds Delete; the same
// was done for the FPB plugin.

using System.Reflection;
using System.Windows;
using System.Windows.Input;
using Aml.Editor.Plugin.OpcUa;
using Aml.Editor.Plugin.OpcUa.Bridge;
using Microsoft.Web.WebView2.Wpf;

namespace PluginTests;

public class ModelerKeysTests
{
    private static void OnUiThread(Action body)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { body(); }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure != null) throw new Xunit.Sdk.XunitException(failure.ToString());
    }

    /// <summary>Delete pressed in <paramref name="view"/>: how often the window's command ran, and whether the page would be told the key is handled.</summary>
    private static (int Executed, bool Handled) PressDelete(WebView2 view)
    {
        var executed = 0;
        var window = new Window
        {
            Width = 200, Height = 200, Left = -10000, Top = -10000,
            ShowActivated = false, ShowInTaskbar = false, WindowStyle = WindowStyle.None,
        };
        window.InputBindings.Add(new KeyBinding(ApplicationCommands.Delete, Key.Delete, ModifierKeys.None));
        window.CommandBindings.Add(new CommandBinding(ApplicationCommands.Delete, (_, _) => executed++));
        window.Content = view;
        window.Show();
        try
        {
            var argsType = typeof(WebView2).Assembly.GetType("Microsoft.Web.WebView2.Wpf.WebView2Base+WebView2KeyEventArgs", throwOnError: true)!;
            var args = (KeyEventArgs)Activator.CreateInstance(argsType,
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null,
                new object[] { Keyboard.PrimaryDevice, PresentationSource.FromVisual(window)!, 0, Key.Delete }, null)!;
            args.RoutedEvent = UIElement.PreviewKeyDownEvent;
            view.RaiseEvent(args);
            return (executed, args.Handled);
        }
        finally
        {
            window.Close();
            view.Dispose();
        }
    }

    [Fact]
    public void The_editor_does_not_see_the_keys_of_the_modeler()
    {
        OnUiThread(() =>
        {
            var (executed, handled) = PressDelete(new PageKeysWebView());
            Assert.Equal(0, executed);
            Assert.False(handled);
        });
    }

    [Fact]
    public void A_plain_WebView_hands_the_keys_to_the_editor()
    {
        // The counter-check: this is how Ctrl+Z reached the editor.
        OnUiThread(() =>
        {
            var (executed, _) = PressDelete(new WebView2());
            Assert.Equal(1, executed);
        });
    }

    [Fact]
    public void The_Modeler_tab_uses_the_view_that_keeps_its_keys()
    {
        Type? type = null;
        OnUiThread(() => type = new OpcUaPlugin().ModelerView.GetType());

        Assert.Equal(typeof(PageKeysWebView), type);
    }
}
