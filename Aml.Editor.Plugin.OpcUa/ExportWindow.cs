// Dialog to choose what the NodeSet export writes: the document by the rules
// of the AutomationML/OPC Foundation working group, or an OPC UA model that
// OPC 10000-83 Annex A put into the document, written back as it was.

using System.Windows;
using System.Windows.Controls;
using System.Xml.Linq;
using OpcUaAml.Export;

namespace Aml.Editor.Plugin.OpcUa;

public sealed class ExportWindow : Window
{
    private readonly RadioButton _document = new() { GroupName = "mode", IsChecked = true };
    private readonly RadioButton _model = new() { GroupName = "mode" };
    private readonly ListBox _namespaces = new() { Height = 120, Margin = new Thickness(22, 4, 0, 0) };

    public ExportMode Mode { get; private set; } = ExportMode.AmlUaXslt;

    /// <summary>For <see cref="ExportMode.AnnexAInverse"/>: the model to write back.</summary>
    public string? NamespaceUri { get; private set; }

    public ExportWindow(XElement caexRoot)
    {
        Width = 620;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;

        var models = AnnexAInverse.Namespaces(caexRoot);
        foreach (var uri in models) _namespaces.Items.Add(DialogKit.Entry(uri, "", uri, "", DialogKit.Exchange));
        if (models.Count > 0) _namespaces.SelectedIndex = 0;

        _document.Content = Option("The document",
            "Instances, classes and libraries by the rules of the AutomationML/OPC Foundation working group (AML-UA-XSLT). "
            + "OPC UA models imported by Annex A become AML classes in UA, not the UA nodes they were.");
        _model.Content = Option("An imported OPC UA model, as it was",
            "The types, DataTypes, ReferenceTypes, methods and objects that OPC 10000-83 Annex A put into the document, "
            + "written back as UA nodes with their NodeIds. Not a standard: the inverse of Annex A. "
            + "What Annex A leaves out (Documentation, type dictionaries, empty strings) does not come back.");
        _model.IsEnabled = models.Count > 0;
        _namespaces.IsEnabled = false;
        _model.Checked += (_, __) => _namespaces.IsEnabled = true;
        _document.Checked += (_, __) => _namespaces.IsEnabled = false;
        _namespaces.MouseDoubleClick += (_, __) => { _model.IsChecked = true; Accept(); };

        var body = new StackPanel();
        body.Children.Add(_document);
        _model.Margin = new Thickness(0, 12, 0, 0);
        body.Children.Add(_model);
        if (models.Count > 0) body.Children.Add(_namespaces);
        else body.Children.Add(new TextBlock { Text = "The document holds no OPC UA model imported by Annex A.", Foreground = DialogKit.Muted, Margin = new Thickness(22, 4, 0, 0) });

        var ok = DialogKit.Action("Export …", primary: true);
        ok.Click += (_, __) => Accept();
        DialogKit.Frame(this, "", DialogKit.Exchange, "Export as OPC UA NodeSet",
            "Choose what the NodeSet holds.", body, null, ok, DialogKit.Action("Cancel", cancel: true));
    }

    private static UIElement Option(string title, string text)
    {
        var panel = new StackPanel { Margin = new Thickness(4, 0, 0, 0) };
        panel.Children.Add(new TextBlock { Text = title, FontWeight = FontWeights.SemiBold });
        panel.Children.Add(new TextBlock { Text = text, Foreground = DialogKit.Muted, TextWrapping = TextWrapping.Wrap, MaxWidth = 540 });
        return panel;
    }

    private void Accept()
    {
        if (_model.IsChecked == true)
        {
            NamespaceUri = DialogKit.Selected<string>(_namespaces);
            if (NamespaceUri == null) return;
            Mode = ExportMode.AnnexAInverse;
        }
        DialogResult = true;
    }
}
