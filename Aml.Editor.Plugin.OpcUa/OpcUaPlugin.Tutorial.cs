// The tutorial: four short lessons on the real plugin, in a panel at its right
// edge. Each step says what to do and frames the control it is about; it is
// done when the plugin's state shows it (a namespace imported, a server
// served), not when the user clicks Next. Steps that need a file or a place
// offer a button that takes the user there. The lessons work in the open
// document, which should be a new, empty one; the first step says so and can
// save one to open.

using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Threading;
using Aml.Editor.Plugin.OpcUa.Diagnostics;
using Aml.Editor.Plugin.OpcUa.Guide;
using Aml.Engine.CAEX;
using Aml.Engine.CAEX.Extensions;
using Microsoft.Win32;
using OpcUaAml.Import;
using OpcUaAml.NodeSets;
using OpcUaAml.Types;

namespace Aml.Editor.Plugin.OpcUa
{
    /// <summary>A step: what to do, the control it is about (x:Name) and its tab, when it is done, and an optional helping hand.</summary>
    internal sealed record TourStep(string Text, string? Target, string? Tab, Func<bool>? Done, string? ActionLabel = null, Action? Action = null);

    internal sealed record TourLesson(string Id, string Title, string Summary, IReadOnlyList<TourStep> Steps);

    public partial class OpcUaPlugin
    {
        private const string DiUri = "http://opcfoundation.org/UA/DI/";

        private TourLesson? _lesson;
        private int _step;
        private DispatcherTimer? _tourTimer;
        private (UIElement Element, HighlightAdorner Adorner)? _highlight;

        // What the lessons watch that the plugin does not keep otherwise.
        private int _checkRuns;
        private readonly HashSet<string> _helpSeen = new(StringComparer.Ordinal);
        private string? _modelerNewUri;

        /// <summary>The lessons, built on demand: their steps look at the plugin's current state.</summary>
        internal IReadOnlyList<TourLesson> TourLessons() => new[]
        {
            new TourLesson("models", Lessons.Titles["models"],
                "Import a companion specification, see what it brings, draw one of its types.",
                new[]
                {
                    new TourStep("The lessons change the open document. Open a new, empty CAEX 3.0 document in the editor first (File, New), "
                                 + "or save a practice document here and open it.",
                        null, "NamespacesTab", () => IsUsable(_document), "Save a practice document…", SavePracticeDocument),
                    new TourStep("Import the Device Integration model (DI), which many companion specifications build on. Click "
                                 + "Import NodeSet… and choose Opc.Ua.Di.NodeSet2.xml; the button below opens the dialog in the folder "
                                 + "that holds it. The first conversion takes a little while.",
                        "ImportButton", "NamespacesTab", () => HasNamespace(DiUri), "Open the dialog in that folder",
                        () => _ = ImportFromFolderAsync(NodeSetCatalog.BundledFolder)),
                    new TourStep("Select the DI namespace in the list. On the right you see what it brings (ObjectTypes, VariableTypes, "
                                 + "DataTypes, ReferenceTypes), what it builds on and which elements use its types.",
                        "NamespaceList", "NamespacesTab", () => (NamespaceList.SelectedItem as NamespaceRow)?.NamespaceUri == DiUri),
                    new TourStep("Draw a type: in the list of types on the right, double click DeviceType. The Diagram tab opens with it.",
                        "NamespaceDetailsPanel", "NamespacesTab", () => _diagram != null),
                    new TourStep("Read the picture: the ? in the Diagram tab explains its shapes, lines and letters.",
                        "DiagramHelp", "DiagramTab", () => _helpSeen.Contains("diagram")),
                }),
            new TourLesson("instances", Lessons.Titles["instances"],
                "Create an instance of a UA type and check it against the type.",
                new[]
                {
                    new TourStep("Click New instance…, search for SoftwareType (from DI), give the instance a name and create it. "
                                 + "Every Mandatory child is created; Optional ones only if you tick them.",
                        "InstanceButton", "NamespacesTab", HasUaInstance),
                    new TourStep("Look at the new instance in the editor's tree (it is selected there): its children, each with the "
                                 + "attributes of its declaration, linked to their parent the way the type says. Click Next when done.",
                        null, "NamespacesTab", null),
                    new TourStep("Click Check. It compares every instance with its type: Mandatory children, placeholders, "
                                 + "abstract types, reference links.",
                        "CheckButton", "NamespacesTab", () => _checkRuns > 0),
                    new TourStep("Try what Check finds: delete one of the Mandatory children of your instance in the editor's tree and "
                                 + "click Check again. Add missing Mandatory children then puts it back. Click Next when done.",
                        "UpgradeButton", "CheckTab", null),
                }),
            new TourLesson("server", Lessons.Titles["server"],
                "Serve this document as an OPC UA server, connect to it, take a part of it back, keep its values live.",
                new[]
                {
                    new TourStep("Give your instance a value to watch: select one of its variables in the editor's tree, Manufacturer "
                                 + "for example, and enter a Value in its attributes. Only elements with a value are kept live later. "
                                 + "No instance yet? Lesson 2 makes one.",
                        null, "ServerTab", HasValuedUaChild),
                    new TourStep("Serve this document: its instance hierarchies become an OPC UA server that only this computer reaches. "
                                 + "It stands in for a real plant here.",
                        "ServeButton", "ServerTab", () => _host != null),
                    new TourStep("Connect to it: the endpoint is filled in. Keep Secure ticked and click Connect. The plugin shows the "
                                 + "server's certificate before it trusts it; trust it, it is your own document server.",
                        "ConnectButton", "ServerTab", () => _client != null),
                    new TourStep("Expand the address space down to your instance and tick its box. Right click it to choose how much "
                                 + "below it to take.",
                        "AddressTree", "ServerTab", () => _items.Count > 0),
                    new TourStep("Click Take into document. The checked part becomes elements in the hierarchy named under 'into', "
                                 + "each with the NodeId of its node.",
                        "MirrorButton", "ServerTab", () => TakenIndex().Count > 0),
                    new TourStep("Click Keep document values live: every change the server reports is written into the elements taken. "
                                 + "Change the value you gave in the editor, and the copy under 'into' follows within a second.",
                        "LiveButton", "ServerTab", () => _live != null),
                    new TourStep("That is the round: model, serve, connect, take, follow. Click Stop serving to end the lesson.",
                        "ServeButton", "ServerTab", () => _host == null),
                }),
            new TourLesson("modeler", Lessons.Titles["modeler"],
                "Model a small information model of your own and apply it to the document.",
                new[]
                {
                    new TourStep("Open the Modeler tab. It edits NodeSets; the document gets the result through an import.",
                        null, "ModelerTab", () => Tabs.SelectedItem == ModelerTab),
                    new TourStep("Enter a namespace URI of your own, such as http://example.org/MyPlant/, and click New model.",
                        "ModelerNewButton", "ModelerTab", () => _modelerNewUri != null),
                    new TourStep("In the modeler, click + next to ObjectTypes and name a type. Select it and add a variable from the "
                                 + "shape's context pad.",
                        "ModelerView", "ModelerTab", () => _modeler?.IsDirty == true),
                    new TourStep("Click Apply to document in the modeler's toolbar. The model is imported like any NodeSet and appears "
                                 + "among the namespaces.",
                        "ModelerView", "ModelerTab", () => _modelerNewUri != null && HasNamespace(_modelerNewUri)),
                }),
        };

        private static bool IsUsable(CAEXDocument? doc) => doc != null && doc.CAEXFile.SchemaVersion == LibraryMerger.RequiredSchemaVersion;

        private bool HasNamespace(string uri) =>
            (NamespaceList.ItemsSource as IEnumerable<NamespaceRow>)?.Any(r => string.Equals(r.NamespaceUri, uri, StringComparison.Ordinal)) == true;

        private bool HasUaInstance() =>
            _document?.CAEXFile.InstanceHierarchy.SelectMany(ih => ih.Descendants<InternalElementType>())
                .Any(ie => UaTypes.IsUaLibraryPath(ie.RefBaseSystemUnitPath)) == true;

        /// <summary>A variable of a UA instance with a value, which the document server can serve and live values follow.</summary>
        private bool HasValuedUaChild() =>
            _document?.CAEXFile.InstanceHierarchy.SelectMany(ih => ih.Descendants<InternalElementType>())
                .Where(ie => UaTypes.IsUaLibraryPath(ie.RefBaseSystemUnitPath))
                .SelectMany(ie => ie.Descendants<InternalElementType>())
                .Any(c => !string.IsNullOrEmpty(c.Attribute["Value"]?.Value)) == true;

        private void InitTutorial()
        {
            HelpButton.Opened += OnHelpOpened;
            HelpButton.LessonRequested += OnLessonRequested;
            ShowTour();
        }

        private void OnHelpOpened(HelpButton button, string topic)
        {
            if (IsAncestorOf(button)) _helpSeen.Add(topic);
        }

        private void OnLessonRequested(HelpButton button, string lesson)
        {
            if (IsAncestorOf(button)) StartLesson(lesson);
        }

        private void TutorialButton_Click(object sender, RoutedEventArgs e)
        {
            if (TourPanel.Visibility == Visibility.Visible && _lesson == null) CloseTour();
            else { _lesson = null; TourPanel.Visibility = Visibility.Visible; ShowTour(); }
        }

        internal void StartLesson(string id)
        {
            _lesson = TourLessons().FirstOrDefault(l => l.Id == id);
            _step = 0;
            TourPanel.Visibility = Visibility.Visible;
            _tourTimer ??= new DispatcherTimer(TimeSpan.FromMilliseconds(500), DispatcherPriority.Background, (_, __) => CheckStep(), Dispatcher);
            _tourTimer.Start();
            ShowTour();
        }

        private void TourClose_Click(object sender, RoutedEventArgs e) => CloseTour();

        private void CloseTour()
        {
            _lesson = null;
            _tourTimer?.Stop();
            ClearHighlight();
            TourPanel.Visibility = Visibility.Collapsed;
        }

        /// <summary>Moves on when the current step's state is reached; lessons are rebuilt so their checks see the present.</summary>
        private void CheckStep()
        {
            if (_lesson == null) { _tourTimer?.Stop(); return; }
            var step = _lesson.Steps[_step];
            bool done;
            try { done = step.Done?.Invoke() == true; }
            catch (Exception ex) { PluginLog.Debug($"Tutorial check: {ex.Message}"); done = false; }
            if (done) Advance();
        }

        private void Advance()
        {
            if (_lesson == null) return;
            if (_step + 1 < _lesson.Steps.Count)
            {
                _step++;
                ShowTour();
                return;
            }
            if (!_settings.CompletedLessons.Contains(_lesson.Id))
            {
                _settings.CompletedLessons.Add(_lesson.Id);
                _settings.Save();
            }
            PluginLog.Info($"Tutorial: lesson '{_lesson.Title}' done.");
            SetStatus($"Lesson done: {_lesson.Title}.");
            _lesson = null;
            _tourTimer?.Stop();
            ShowTour();
        }

        private void ShowTour()
        {
            var palette = ThemePalette.Current(this);
            var body = TourBody;
            body.Children.Clear();
            ClearHighlight();
            if (_lesson == null)
            {
                TourTitle.Text = "Tutorial";
                body.Children.Add(Paragraph("Four short lessons on the plugin, each a few minutes, in the open document. Steps tick "
                                            + "themselves off when done; the control a step is about is framed in orange.", palette.Muted));
                foreach (var lesson in TourLessons())
                {
                    var done = _settings.CompletedLessons.Contains(lesson.Id);
                    var card = new StackPanel { Margin = new Thickness(0, 12, 0, 0) };
                    card.Children.Add(new TextBlock
                    {
                        Text = (done ? "✓ " : "") + lesson.Title, FontWeight = FontWeights.SemiBold,
                        Foreground = done ? palette.Create : palette.Foreground,
                    });
                    card.Children.Add(Paragraph(lesson.Summary, palette.Muted));
                    var start = DialogKit.Action(done ? "Again" : "Start", primary: false);
                    start.HorizontalAlignment = HorizontalAlignment.Left;
                    start.Margin = new Thickness(0, 4, 0, 0);
                    var id = lesson.Id;
                    start.Click += (_, __) => StartLesson(id);
                    card.Children.Add(start);
                    body.Children.Add(card);
                }
                return;
            }

            var step = _lesson.Steps[_step];
            TourTitle.Text = _lesson.Title;
            body.Children.Add(new TextBlock { Text = $"Step {_step + 1} of {_lesson.Steps.Count}", Foreground = palette.Muted, Margin = new Thickness(0, 0, 0, 6) });
            var progress = new ProgressBar { Height = 4, Maximum = _lesson.Steps.Count, Value = _step, Margin = new Thickness(0, 0, 0, 10) };
            body.Children.Add(progress);
            body.Children.Add(Paragraph(step.Text, palette.Foreground));
            if (step.Action != null)
            {
                var act = DialogKit.Action(step.ActionLabel ?? "Do it", primary: true);
                act.HorizontalAlignment = HorizontalAlignment.Left;
                act.Margin = new Thickness(0, 10, 0, 0);
                act.Click += (_, __) => step.Action();
                body.Children.Add(act);
            }
            var row = new WrapPanel { Margin = new Thickness(0, 14, 0, 0) };
            if (step.Target != null)
            {
                var show = DialogKit.Action("Show me");
                show.Click += (_, __) => Highlight(step);
                row.Children.Add(show);
            }
            var next = DialogKit.Action(step.Done == null ? "Next" : "Skip step");
            next.Margin = new Thickness(6, 0, 0, 0);
            next.Click += (_, __) => Advance();
            row.Children.Add(next);
            body.Children.Add(row);
            var back = new Hyperlink(new Run("All lessons")) { Foreground = palette.Exchange };
            back.Click += (_, __) => { _lesson = null; _tourTimer?.Stop(); ShowTour(); };
            body.Children.Add(new TextBlock(back) { Margin = new Thickness(0, 16, 0, 0) });
            Highlight(step);
        }

        private static TextBlock Paragraph(string text, Brush brush) =>
            new() { Text = text, TextWrapping = TextWrapping.Wrap, Foreground = brush, Margin = new Thickness(0, 2, 0, 0) };

        /// <summary>Opens the step's tab and frames its control, once the tab has laid it out.</summary>
        private void Highlight(TourStep step)
        {
            ClearHighlight();
            if (step.Tab != null && FindName(step.Tab) is TabItem tab) Tabs.SelectedItem = tab;
            if (step.Target == null || FindName(step.Target) is not FrameworkElement target) return;
            Dispatcher.BeginInvoke(() =>
            {
                if (!target.IsVisible || System.Windows.Documents.AdornerLayer.GetAdornerLayer(target) is not { } layer) return;
                target.BringIntoView();
                var adorner = new HighlightAdorner(target);
                layer.Add(adorner);
                _highlight = (target, adorner);
            }, DispatcherPriority.Loaded);
        }

        private void ClearHighlight()
        {
            if (_highlight is { } h && System.Windows.Documents.AdornerLayer.GetAdornerLayer(h.Element) is { } layer) layer.Remove(h.Adorner);
            _highlight = null;
        }

        /// <summary>An empty CAEX 3.0 document to open in the editor for the lessons.</summary>
        private void SavePracticeDocument()
        {
            var dialog = new SaveFileDialog { Title = "Save a practice document", Filter = "AutomationML (*.aml)|*.aml", FileName = "OpcUaTutorial.aml" };
            if (dialog.ShowDialog() != true) return;
            Guard("Saving the practice document", () =>
            {
                var doc = CAEXDocument.New_CAEXDocument(CAEXDocument.CAEXSchema.CAEX3_0);
                doc.CAEXFile.InstanceHierarchy.Append("Plant");
                doc.SaveToFile(dialog.FileName, true);
                SetStatus($"Saved {Path.GetFileName(dialog.FileName)}. Open it in the editor (File, Open); the lesson goes on by itself.");
            });
        }
    }
}

namespace Aml.Editor.Plugin.OpcUa.Guide
{
    internal static class Lessons
    {
        public static readonly IReadOnlyDictionary<string, string> Titles = new Dictionary<string, string>
        {
            ["models"] = "1  Models into the document",
            ["instances"] = "2  Instances and checks",
            ["server"] = "3  A running server",
            ["modeler"] = "4  A model of your own",
        };
    }
}
