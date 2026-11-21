// Dialog to pick an InternalElement of the document, for binding a node to it.

using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Aml.Editor.Plugin.Contracts;
using Aml.Editor.Plugin.OpcUa.Diagnostics;
using Aml.Editor.Plugin.OpcUa.Localization;
using Aml.Engine.CAEX;
using Aml.Engine.CAEX.Extensions;
using Microsoft.Win32;
using OpcUaAml.Addressing;
using OpcUaAml.NodeSets;
using OpcUaAml.Server;

namespace Aml.Editor.Plugin.OpcUa;

/// <summary>Picks an InternalElement of the document's instance hierarchies.</summary>
public sealed class ElementPickerWindow : Window
{
    private readonly ListBox _list = new();
    private readonly TextBox _search = new();
    private readonly List<(string Name, string Parent, InternalElementType Element)> _all;

    public InternalElementType? Selected { get; private set; }

    /// <summary>The text in the search field.</summary>
    internal string SearchText => _search.Text;

    /// <summary>The element chosen in the list, before Bind is clicked.</summary>
    internal InternalElementType? Chosen => DialogKit.Selected<InternalElementType>(_list);

    /// <param name="preselect">The element selected in the editor: searched for and chosen already, when it is one of the document's.</param>
    public ElementPickerWindow(CAEXDocument document, string title, InternalElementType? preselect = null)
    {
        Width = 620;
        Height = 520;
        ResizeMode = ResizeMode.CanResizeWithGrip;
        _all = document.CAEXFile.InstanceHierarchy
            .SelectMany(ih => ih.Descendants<InternalElementType>().Select(ie => (ie.Name, ParentPath(ie), ie)))
            .ToList();
        _search.TextChanged += (_, __) => Filter();
        _list.MouseDoubleClick += (_, __) => Accept();

        var ok = DialogKit.Action(L.T("Bind"), primary: true);
        ok.Click += (_, __) => Accept();
        var search = DialogKit.WithPlaceholder(_search, L.T("Search elements by name or path"));
        ((FrameworkElement)search).Margin = new Thickness(0, 0, 0, 6);
        var body = new DockPanel();
        DockPanel.SetDock(search, Dock.Top);
        body.Children.Add(search);
        body.Children.Add(_list);

        DialogKit.Frame(this, "\uE71B", DialogKit.Relate, title,
            L.T("The element you choose gets the node's address as its NodeId attribute (OPC 10000-83 Annex A)."),
            body, null, ok, DialogKit.Action(L.T("Cancel"), cancel: true));
        Filter();
        if (preselect != null && _all.Any(x => ReferenceEquals(x.Element.Node, preselect.Node)))
        {
            _search.Text = preselect.Name;
            _list.SelectedItem = _list.Items.OfType<ListBoxItem>()
                .FirstOrDefault(i => i.Tag is InternalElementType e && ReferenceEquals(e.Node, preselect.Node));
            if (_list.SelectedItem != null) _list.ScrollIntoView(_list.SelectedItem);
        }
        // Enter binds the chosen element; typing replaces the search.
        Loaded += (_, __) => { _search.Focus(); _search.SelectAll(); };
    }

    private void Accept()
    {
        Selected = DialogKit.Selected<InternalElementType>(_list);
        if (Selected != null) DialogResult = true;
    }

    private void Filter()
    {
        var text = _search.Text.Trim();
        _list.ItemsSource = _all
            .Where(x => text.Length == 0 || $"{x.Parent}/{x.Name}".Contains(text, StringComparison.OrdinalIgnoreCase))
            .Select(x => DialogKit.Entry(x.Name, x.Parent, x.Element)).ToList();
    }

    /// <summary>"Hierarchy/Parent": where the element is.</summary>
    private static string ParentPath(InternalElementType ie)
    {
        var parts = new List<string>();
        for (var o = ie.CAEXParent as CAEXBasicObject; o is CAEXObject c; o = c.CAEXParent as CAEXBasicObject)
        {
            parts.Insert(0, c.Name);
            if (c is InstanceHierarchyType) break;
        }
        return string.Join("/", parts);
    }
}
