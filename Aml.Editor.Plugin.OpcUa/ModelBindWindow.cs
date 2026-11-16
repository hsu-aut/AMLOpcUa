// Bind model…: a variable of the server proposed for each value of a model of
// one's own (OpcUaAml.Binding), checked in one table and bound at once. The
// sure proposals are ticked; for the others the user picks a candidate or
// leaves the row. A report lists every value and the variables no value got.

using System.ComponentModel;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using Aml.Editor.Plugin.OpcUa.Localization;
using Aml.Engine.CAEX;
using Aml.Engine.CAEX.Extensions;
using Microsoft.Win32;
using OpcUaAml.Binding;
using OpcUaAml.Server;
using OpcUaAml.Types;

namespace Aml.Editor.Plugin.OpcUa;

/// <summary>A variable to pick for a value, or none.</summary>
public sealed record ModelBindChoice(ServerVariable? Variable, string Label)
{
    public override string ToString() => Label;
}

/// <summary>One value of the model in the table: the variable chosen for it, and whether it is bound.</summary>
public sealed class ModelBindRow : INotifyPropertyChanged
{
    private ModelBindChoice _chosen;
    private bool _keep;

    public ModelBindRow(BindingProposal proposal)
    {
        Proposal = proposal;
        var none = new ModelBindChoice(null, L.T("(none)"));
        Choices = proposal.BoundAlready
            ? [new ModelBindChoice(proposal.Variable, proposal.Variable?.PathText ?? L.T("(a browse path or alias)"))]
            : [.. proposal.Candidates.Select(v => new ModelBindChoice(v, v.PathText)), none];
        _chosen = Choices.FirstOrDefault(c => c.Variable != null && c.Variable == proposal.Variable) ?? Choices[^1];
        _keep = proposal.Keep;
        Match = proposal.Confidence switch
        {
            BindingConfidence.Sure => L.T("sure"),
            BindingConfidence.Likely => L.T("likely"),
            BindingConfidence.Check => L.T("check"),
            BindingConfidence.Bound => L.T("bound already"),
            _ => L.T("none found"),
        };
        Shared = string.Join(", ", proposal.Shared);
    }

    public BindingProposal Proposal { get; }
    public string ModelPath => Proposal.Signal.PathText;
    public IReadOnlyList<ModelBindChoice> Choices { get; }
    public string Match { get; }
    public string Shared { get; }

    public ModelBindChoice Chosen
    {
        get => _chosen;
        set
        {
            if (value == null || value == _chosen) return;
            _chosen = value;
            // Picking a variable is choosing to bind it; picking none is not.
            Keep = value.Variable != null && !Proposal.BoundAlready;
            Changed(nameof(Chosen));
            Changed(nameof(CanKeep));
        }
    }

    /// <summary>A row can be ticked when a variable is chosen and the element is not bound yet.</summary>
    public bool CanKeep => !Proposal.BoundAlready && _chosen.Variable != null;

    public bool Keep
    {
        get => _keep && CanKeep;
        set
        {
            if (value == _keep) return;
            _keep = value;
            Changed(nameof(Keep));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Changed(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    /// <summary>What a screen reader says for the row.</summary>
    public override string ToString() => $"{ModelPath}: {_chosen.Label}, {Match}";
}

/// <summary>The table's rows from a plan, what they bind, the report, and which models there are.</summary>
public static class ModelBindRows
{
    public static IReadOnlyList<ModelBindRow> From(BindingPlan plan) => plan.Proposals.Select(p => new ModelBindRow(p)).ToList();

    /// <summary>The proposals to bind: the ticked rows, each with the variable chosen in it.</summary>
    public static IReadOnlyList<BindingProposal> Kept(IEnumerable<ModelBindRow> rows) =>
        rows.Where(r => r.Keep).Select(r => r.Proposal with { Variable = r.Chosen.Variable }).ToList();

    /// <summary>Every value with its variable, then the variables no value got; semicolons, as spreadsheets here read them.</summary>
    public static string Report(BindingPlan plan, IEnumerable<ModelBindRow> rows)
    {
        var text = new StringBuilder();
        void Line(params string?[] cells) => text.Append(string.Join(";", cells.Select(Cell))).Append(Environment.NewLine);
        Line(L.T("Value of the model"), L.T("Variable of the server"), "NodeId", L.T("Match"), L.T("Bind"), L.T("Words in common"));
        var used = new HashSet<ServerVariable>();
        foreach (var r in rows)
        {
            var v = r.Chosen.Variable;
            if (v != null && (r.Keep || r.Proposal.BoundAlready)) used.Add(v);
            Line(r.ModelPath, v?.PathText, v?.Address.ToString(), r.Match, r.Keep ? L.T("yes") : L.T("no"), r.Shared);
        }
        text.Append(Environment.NewLine);
        Line(L.T("Variables without a value of the model"), "NodeId");
        // Every variable is either unmatched or proposed for a value; those the rows do not bind are left.
        foreach (var v in plan.Unmatched.Concat(plan.Proposals.Select(p => p.Variable).OfType<ServerVariable>()).Distinct().Where(v => !used.Contains(v)))
            Line(v.PathText, v.Address.ToString());
        return text.ToString();
    }

    private static string Cell(string? s) =>
        s == null ? "" : s.IndexOfAny([';', '"', '\n', '\r']) >= 0 ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;

    /// <summary>
    /// An element of the user's own: not below an element taken from a server
    /// and not an instance of a UA type, whose values the server describes itself.
    /// </summary>
    public static bool IsOwn(InternalElementType element)
    {
        for (var e = element; e != null; e = e.CAEXParent as InternalElementType)
            if (UaTypes.IsUaLibraryPath(e.RefBaseSystemUnitPath) || e.Attribute[AddressSpaceMirror.EndpointAttribute] != null) return false;
        return true;
    }

    /// <summary>The instance hierarchies that hold elements of the user's own.</summary>
    public static IReadOnlyList<CAEXObject> Models(CAEXDocument document) =>
        document.CAEXFile.InstanceHierarchy.Where(ih => ih.Descendants<InternalElementType>().Any(IsOwn)).Cast<CAEXObject>().ToList();

    /// <summary>The signals of a model that are the user's own.</summary>
    public static IReadOnlyList<ModelSignal> Signals(CAEXObject model) =>
        ModelBinder.Signals(model).Where(s => s.Element == null || IsOwn(s.Element)).ToList();
}

/// <summary>The dialog of Bind model….</summary>
public sealed class ModelBindWindow : Window
{
    private readonly CollectedVariables _variables;
    private readonly ComboBox _models = new() { MinWidth = 260 };
    private readonly ListView _table = new();
    private readonly TextBlock _summary = DialogKit.Message();
    private readonly Button _bind = DialogKit.Action(L.T("Bind"), primary: true);
    private BindingPlan _plan = new([], []);
    private IReadOnlyList<ModelBindRow> _rows = [];

    /// <summary>The proposals to bind, once the dialog is closed with Bind.</summary>
    public IReadOnlyList<BindingProposal> Kept { get; private set; } = [];

    public string ModelName => (_models.SelectedItem as ComboBoxItem)?.Content as string ?? "";

    public ModelBindWindow(IReadOnlyList<CAEXObject> models, CAEXObject? preferred, CollectedVariables variables, string serverPart)
    {
        _variables = variables;
        Width = 1000;
        Height = 640;
        ResizeMode = ResizeMode.CanResizeWithGrip;

        foreach (var m in models) _models.Items.Add(new ComboBoxItem { Content = m.Name, Tag = m });
        _models.SelectedIndex = Math.Max(0, models.ToList().FindIndex(m => preferred != null && m.Node == preferred.Node));
        _models.SelectionChanged += (_, _) => Propose();
        AutomationProperties.SetName(_models, L.T("Model"));

        var top = new WrapPanel { Margin = new Thickness(0, 0, 0, 8) };
        top.Children.Add(new TextBlock { Text = L.T("Model"), FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) });
        top.Children.Add(_models);
        top.Children.Add(new TextBlock
        {
            Text = L.T("Server part") + ": " + serverPart, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(18, 0, 0, 0),
            Foreground = DialogKit.Muted,
        });

        var grid = new GridView();
        grid.Columns.Add(new GridViewColumn { Header = L.T("Bind"), Width = 56, CellTemplate = Check() });
        grid.Columns.Add(new GridViewColumn { Header = L.T("Value of the model"), Width = 260, DisplayMemberBinding = new Binding(nameof(ModelBindRow.ModelPath)) });
        grid.Columns.Add(new GridViewColumn { Header = L.T("Variable of the server"), Width = 380, CellTemplate = Choice() });
        grid.Columns.Add(new GridViewColumn { Header = L.T("Match"), Width = 100, DisplayMemberBinding = new Binding(nameof(ModelBindRow.Match)) });
        grid.Columns.Add(new GridViewColumn { Header = L.T("Words in common"), Width = 170, DisplayMemberBinding = new Binding(nameof(ModelBindRow.Shared)) });
        _table.View = grid;
        AutomationProperties.SetName(_table, L.T("Values of the model and their variables"));
        VirtualizingPanel.SetIsVirtualizing(_table, true);

        var body = new DockPanel();
        DockPanel.SetDock(top, Dock.Top);
        body.Children.Add(top);
        if (variables.Truncated)
        {
            var limit = new TextBlock
            {
                Text = L.T("The search stopped after {0} variables; a variable further on may be missing. Select a part of the server in the address space to narrow it.", variables.Variables.Count),
                TextWrapping = TextWrapping.Wrap, Foreground = DialogKit.Danger, Margin = new Thickness(0, 0, 0, 8),
            };
            DockPanel.SetDock(limit, Dock.Top);
            body.Children.Add(limit);
        }
        DockPanel.SetDock(_summary, Dock.Bottom);
        _summary.Margin = new Thickness(0, 8, 0, 0);
        body.Children.Add(_summary);
        body.Children.Add(_table);

        _bind.Click += (_, _) =>
        {
            Kept = ModelBindRows.Kept(_rows);
            if (Kept.Count > 0) DialogResult = true;
        };
        var report = DialogKit.Action(L.T("Save report…"));
        report.Click += (_, _) => SaveReport();

        DialogKit.Frame(this, "", DialogKit.Relate, L.T("Bind a model to the server"),
            L.T("For each value of the model, the variable whose path has the same words is proposed. Ticked rows are bound: the element gets the variable's address as its NodeId attribute and a Value attribute, as with Bind to element…."),
            body, report, _bind, DialogKit.Action(L.T("Cancel"), cancel: true));
        Propose();
    }

    private void Propose()
    {
        if ((_models.SelectedItem as ComboBoxItem)?.Tag is not CAEXObject model) return;
        _plan = ModelBinder.Propose(ModelBindRows.Signals(model), _variables.Variables);
        foreach (var r in _rows) r.PropertyChanged -= RowChanged;
        _rows = ModelBindRows.From(_plan);
        foreach (var r in _rows) r.PropertyChanged += RowChanged;
        _table.ItemsSource = _rows;
        Summarize();
    }

    private void RowChanged(object? sender, PropertyChangedEventArgs e) => Summarize();

    private void Summarize()
    {
        var kept = _rows.Count(r => r.Keep);
        var check = _rows.Count(r => !r.Keep && r.CanKeep);
        var none = _rows.Count(r => r.Chosen.Variable == null && !r.Proposal.BoundAlready);
        var bound = _rows.Count(r => r.Proposal.BoundAlready);
        var used = new HashSet<ServerVariable>(_rows.Where(r => r.Keep || r.Proposal.BoundAlready).Select(r => r.Chosen.Variable).OfType<ServerVariable>());
        _summary.Text = L.T("{0} of {1} value(s) ticked, {2} to check, {3} without a variable, {4} bound already; {5} variable(s) of the server have no value of the model.",
            kept, _rows.Count, check, none, bound, _variables.Variables.Count(v => !used.Contains(v)));
        _bind.Content = L.T("Bind {0}", kept);
        _bind.IsEnabled = kept > 0;
    }

    private void SaveReport()
    {
        var dialog = new SaveFileDialog
        {
            Title = L.T("Save the binding report"),
            Filter = L.T("CSV, semicolons (*.csv)|*.csv"),
            FileName = $"{ModelName}-binding.csv",
        };
        if (dialog.ShowDialog(this) != true) return;
        File.WriteAllText(dialog.FileName, ModelBindRows.Report(_plan, _rows), new UTF8Encoding(true));
        DialogKit.ShowInfo(_summary, L.T("Saved the report to {0}.", dialog.FileName));
    }

    private static DataTemplate Check()
    {
        var box = new FrameworkElementFactory(typeof(CheckBox));
        box.SetBinding(ToggleButton.IsCheckedProperty, new Binding(nameof(ModelBindRow.Keep)) { Mode = BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged });
        box.SetBinding(IsEnabledProperty, new Binding(nameof(ModelBindRow.CanKeep)));
        box.SetBinding(AutomationProperties.NameProperty, new Binding(nameof(ModelBindRow.ModelPath)));
        return new DataTemplate { VisualTree = box };
    }

    private static DataTemplate Choice()
    {
        var box = new FrameworkElementFactory(typeof(ComboBox));
        box.SetBinding(ItemsControl.ItemsSourceProperty, new Binding(nameof(ModelBindRow.Choices)));
        box.SetBinding(Selector.SelectedItemProperty, new Binding(nameof(ModelBindRow.Chosen)) { Mode = BindingMode.TwoWay });
        box.SetValue(MinWidthProperty, 340.0);
        box.SetBinding(AutomationProperties.NameProperty, new Binding(nameof(ModelBindRow.ModelPath)));
        return new DataTemplate { VisualTree = box };
    }
}
