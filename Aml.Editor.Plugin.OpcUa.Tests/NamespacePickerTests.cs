using System.Windows;
using System.Windows.Controls;
using Aml.Editor.Plugin.OpcUa;
using Aml.Editor.Plugin.OpcUa.Localization;
using OpcUaAml.Server;

namespace PluginTests;

/// <summary>
/// "Types of the server…": the objects of a namespace are for the modeler or
/// a file. Ticked and then imported, they were browsed for nothing (slow on a
/// large server) and reported as "instance(s) not taken".
/// </summary>
public class NamespacePickerTests
{
    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
        {
            if (child is T t) yield return t;
            foreach (var d in Descendants<T>(child)) yield return d;
        }
    }

    /// <summary>Ticks the objects and answers with the button named <paramref name="button"/>.</summary>
    private static (NamespaceAction Action, bool Objects) Answer(string button)
    {
        NamespaceAction action = default;
        bool objects = false;
        Exception? failure = null;
        var german = L.German;
        L.German = false;
        try
        {
            var thread = new Thread(() =>
            {
                try
                {
                    var window = new NamespacePickerWindow(new[] { new ServerNamespace("http://example.org/Plant/", 2, "1.0.0", null, true) }, canImport: true)
                    {
                        Left = -4000, ShowInTaskbar = false,
                    };
                    window.Loaded += (_, _) => window.Dispatcher.BeginInvoke(() =>
                    {
                        Descendants<CheckBox>(window).Single().IsChecked = true;
                        Descendants<Button>(window).First(b => b.Content as string == button).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    });
                    window.ShowDialog();
                    action = window.Action;
                    objects = window.IncludeInstances;
                }
                catch (Exception ex) { failure = ex; }
            });
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            thread.Join();
        }
        finally { L.German = german; }
        if (failure != null) throw new Xunit.Sdk.XunitException(failure.ToString());
        return (action, objects);
    }

    [Fact]
    public void An_import_does_not_fetch_the_objects_it_leaves_out()
    {
        var (action, objects) = Answer("Import types");

        Assert.Equal(NamespaceAction.Import, action);
        Assert.False(objects);
    }

    [Fact]
    public void The_modeler_gets_the_objects_when_they_are_ticked()
    {
        var (action, objects) = Answer("Open in modeler");

        Assert.Equal(NamespaceAction.Modeler, action);
        Assert.True(objects);
    }
}
