// Dialog listing the namespaces of a connected server: import one's NodeSet,
// open it in the modeler, or save it.

using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Aml.Editor.Plugin.Contracts;
using Aml.Editor.Plugin.OpcUa.Diagnostics;
using Aml.Engine.CAEX;
using Aml.Engine.CAEX.Extensions;
using Microsoft.Win32;
using OpcUaAml.Addressing;
using OpcUaAml.NodeSets;
using OpcUaAml.Server;

namespace Aml.Editor.Plugin.OpcUa;

public enum NamespaceAction { Import, Modeler, Save }

/// <summary>Picks a namespace of a server and what to do with its NodeSet.</summary>
public sealed class NamespacePickerWindow : Window
{
    private readonly ListView _list = new()
    {
        View = new GridView
        {
            Columns =
            {
                new GridViewColumn { Header = "Namespace", Width = 340, DisplayMemberBinding = new System.Windows.Data.Binding(nameof(Row.Uri)) },
                new GridViewColumn { Header = "Version", Width = 70, DisplayMemberBinding = new System.Windows.Data.Binding(nameof(Row.Version)) },
                new GridViewColumn { Header = "Published", Width = 84, DisplayMemberBinding = new System.Windows.Data.Binding(nameof(Row.Published)) },
                new GridViewColumn { Header = "NodeSet", Width = 110, DisplayMemberBinding = new System.Windows.Data.Binding(nameof(Row.Source)) },
            },
        },
    };
    private readonly CheckBox _instances = new()
    {
        Content = "Include the namespace's objects",
        ToolTip = "For the modeler or a file; an import takes the types, the objects come in through 'Take into document'.",
    };

    public ServerNamespace? Selected { get; private set; }
    public NamespaceAction Action { get; private set; }
    public bool IncludeInstances => _instances.IsChecked == true;

    public NamespacePickerWindow(IReadOnlyList<ServerNamespace> namespaces, bool canImport)
    {
        Width = 740;
        Height = 460;
        ResizeMode = ResizeMode.CanResizeWithGrip;
        _list.ItemsSource = namespaces.Select(n => new Row(n)).ToList();
        // Namespaces with metadata are models; the server's own namespace usually has none.
        var first = namespaces.Select((n, i) => (n, i)).FirstOrDefault(x => x.n.Version != null);
        _list.SelectedIndex = namespaces.Count == 0 ? -1 : first.n != null ? first.i : 0;

        Button Make(string text, NamespaceAction action, bool enabled, bool primary)
        {
            var b = DialogKit.Action(text, primary);
            b.IsEnabled = enabled;
            b.Click += (_, __) => Accept(action);
            return b;
        }
        var defaultAction = canImport ? NamespaceAction.Import : NamespaceAction.Modeler;
        _list.MouseDoubleClick += (_, __) => Accept(defaultAction);

        DialogKit.Frame(this, "\uE8B5", DialogKit.Exchange, "Types of the server",
            "Where the server publishes a namespace's NodeSet, that file is taken; otherwise the NodeSet is rebuilt by browsing, without documentation links and without nodes no reference leads to.",
            _list, _instances,
            Make("Import types", NamespaceAction.Import, canImport, canImport),
            Make("Open in modeler", NamespaceAction.Modeler, true, !canImport),
            Make("Save as…", NamespaceAction.Save, true, false),
            DialogKit.Action("Cancel", cancel: true));
    }

    private void Accept(NamespaceAction action)
    {
        Selected = (_list.SelectedItem as Row)?.Namespace;
        Action = action;
        if (Selected != null) DialogResult = true;
    }

    private sealed record Row(ServerNamespace Namespace)
    {
        public string Uri => Namespace.Uri;
        public string Version => Namespace.Version ?? "";
        public string Published => Namespace.PublicationDate is { } d ? d.ToString("yyyy-MM-dd") : "";
        public string Source => Namespace.HasFile ? "published file" : "by browsing";
        public override string ToString() => Uri;
    }
}
