// Save events…: as a CSV file, or as an attachment of an element in the
// document. The attachment is preselected for a container (.amlx), which the
// editor completes on saving; beside an .aml document it is a file to pass on
// with the document, which the dialog says.

using System.Windows;
using System.Windows.Controls;
using Aml.Editor.Plugin.OpcUa.Localization;
using OpcUaAml.Import;

namespace Aml.Editor.Plugin.OpcUa;

public sealed class SaveEventsWindow : Window
{
    public RadioButton CsvChoice { get; } = new() { GroupName = "Where", Content = L.T("As a CSV file") };
    public RadioButton AttachmentChoice { get; } = new() { GroupName = "Where", Margin = new Thickness(0, 6, 0, 0) };
    private readonly TextBlock _hint = new() { TextWrapping = TextWrapping.Wrap, Foreground = DialogKit.Muted, Margin = new Thickness(20, 3, 0, 0) };

    /// <param name="documentPath">Where the document is saved; null while it is not, and no attachment can go beside it.</param>
    /// <param name="elementName">The element the attachment goes to.</param>
    /// <param name="pickElement">Lets the user choose another element; returns its name, or null when the choice was cancelled.</param>
    public SaveEventsWindow(string? documentPath, string elementName, Func<string?>? pickElement = null)
    {
        Width = 520;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        SetElement(elementName);
        var container = documentPath != null && AmlContainers.IsContainerPath(documentPath);
        AttachmentChoice.IsEnabled = documentPath != null;
        (container ? AttachmentChoice : CsvChoice).IsChecked = true;
        _hint.Text = documentPath == null ? L.T("Save the document first: an attachment is a file beside it.")
            : container ? L.T("The attachment is a file beside the container. When you save the container (Ctrl+S), the editor offers to take it in; keep it ticked there.")
            : L.T("The attachment is a file beside the document; pass it on with the document.");

        var body = new StackPanel();
        body.Children.Add(CsvChoice);
        var attachment = new DockPanel();
        if (pickElement != null && documentPath != null)
        {
            var other = new Button { Content = L.T("Element…"), Margin = new Thickness(8, 6, 0, 0), Padding = new Thickness(8, 1, 8, 1) };
            other.Click += (_, _) =>
            {
                if (pickElement() is { } name)
                {
                    SetElement(name);
                    AttachmentChoice.IsChecked = true;
                }
            };
            DockPanel.SetDock(other, Dock.Right);
            attachment.Children.Add(other);
        }
        attachment.Children.Add(AttachmentChoice);
        body.Children.Add(attachment);
        body.Children.Add(_hint);

        var save = DialogKit.Action(L.T("Save"), primary: true);
        save.Click += (_, _) => DialogResult = true;
        DialogKit.Frame(this, "", DialogKit.Plain, L.T("Save events"),
            L.T("Every row of the list, the oldest first, with the server each came from."),
            body, null, save, DialogKit.Action(L.T("Cancel"), cancel: true));
    }

    private void SetElement(string name) => AttachmentChoice.Content = L.T("As an attachment of '{0}', linked in the document", name);
}
