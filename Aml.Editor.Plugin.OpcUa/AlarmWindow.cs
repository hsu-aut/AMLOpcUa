// Create alarm…: an alarm of OPC UA Part 9 for the value selected in the
// editor. A number gets a limit alarm (above or below), a boolean a state
// alarm with its normal value. The name and the text are proposed from the
// value and follow the kind until the user writes their own.

using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Aml.Editor.Plugin.OpcUa.Localization;
using Aml.Engine.CAEX;
using OpcUaAml.Alarms;
using OpcUaAml.Server;

namespace Aml.Editor.Plugin.OpcUa;

/// <summary>What the dialog asks to create.</summary>
public sealed record AlarmRequest(AlarmKind Kind, double? Limit, bool? Normal, ushort Severity, string Message, string Name);

public sealed class AlarmWindow : Window
{
    private readonly InternalElementType _value;
    private bool _nameOwn, _messageOwn, _proposing;

    public RadioButton KindHigh { get; } = new() { Content = L.T("Above a limit"), GroupName = "Kind" };
    public RadioButton KindLow { get; } = new() { Content = L.T("Below a limit"), GroupName = "Kind", Margin = new Thickness(12, 0, 0, 0) };
    public RadioButton KindState { get; } = new() { Content = L.T("State (true/false)"), GroupName = "Kind", Margin = new Thickness(12, 0, 0, 0) };
    public TextBox LimitBox { get; } = new() { Width = 120, HorizontalAlignment = HorizontalAlignment.Left };
    public ComboBox SeverityBox { get; } = new() { Width = 180, HorizontalAlignment = HorizontalAlignment.Left };
    public TextBox CustomSeverityBox { get; } = new() { Width = 60, Margin = new Thickness(8, 0, 0, 0), Visibility = Visibility.Collapsed };
    public RadioButton NormalFalse { get; } = new() { Content = "false", GroupName = "Normal", IsChecked = true };
    public RadioButton NormalTrue { get; } = new() { Content = "true", GroupName = "Normal", Margin = new Thickness(12, 0, 0, 0) };
    public TextBox MessageBox { get; } = new() { MinWidth = 320 };
    public TextBox NameBox { get; } = new() { MinWidth = 320 };
    public TextBlock Error { get; } = DialogKit.Message();

    /// <summary>What Create asked for, once accepted.</summary>
    public AlarmRequest? Request { get; private set; }

    private static readonly ushort[] Severities = [200, 500, 800];

    public AlarmWindow(InternalElementType value)
    {
        _value = value;
        Width = 560;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        var boolean = value.Attribute["Value"]?.AttributeDataType == "xs:boolean";
        KindHigh.IsEnabled = KindLow.IsEnabled = !boolean;
        KindState.IsEnabled = boolean;
        (boolean ? KindState : KindHigh).IsChecked = true;
        foreach (var text in new[] { L.T("Info (200)"), L.T("Warning (500)"), L.T("Fault (800)"), L.T("Custom…") })
            SeverityBox.Items.Add(new ComboBoxItem { Content = text });
        SeverityBox.SelectedIndex = 2;
        SeverityBox.SelectionChanged += (_, _) => CustomSeverityBox.Visibility = SeverityBox.SelectedIndex == 3 ? Visibility.Visible : Visibility.Collapsed;
        AutomationProperties.SetName(LimitBox, L.T("Limit"));
        AutomationProperties.SetName(SeverityBox, L.T("Severity"));
        AutomationProperties.SetName(CustomSeverityBox, L.T("Severity, 1 to 1000"));
        AutomationProperties.SetName(MessageBox, L.T("Text"));
        AutomationProperties.SetName(NameBox, L.T("Name"));
        foreach (var kind in new[] { KindHigh, KindLow, KindState }) kind.Checked += (_, _) => Propose();
        NameBox.TextChanged += (_, _) => { if (!_proposing) _nameOwn = true; };
        MessageBox.TextChanged += (_, _) => { if (!_proposing) _messageOwn = true; };
        Propose();

        var body = new StackPanel();
        body.Children.Add(DialogKit.Facts((L.T("Value"), PathOf(value), false)));
        body.Children.Add(DialogKit.Label(L.T("Kind")));
        body.Children.Add(Row(KindHigh, KindLow, KindState));
        var limit = new StackPanel { Visibility = boolean ? Visibility.Collapsed : Visibility.Visible };
        limit.Children.Add(DialogKit.Label(L.T("Limit")));
        limit.Children.Add(LimitBox);
        body.Children.Add(limit);
        var normal = new StackPanel { Visibility = boolean ? Visibility.Visible : Visibility.Collapsed };
        normal.Children.Add(DialogKit.Label(L.T("Normal is")));
        normal.Children.Add(Row(NormalFalse, NormalTrue));
        body.Children.Add(normal);
        body.Children.Add(DialogKit.Label(L.T("Severity")));
        body.Children.Add(Row(SeverityBox, CustomSeverityBox));
        body.Children.Add(DialogKit.Label(L.T("Text")));
        body.Children.Add(MessageBox);
        body.Children.Add(DialogKit.Label(L.T("Name")));
        body.Children.Add(NameBox);
        Error.Margin = new Thickness(0, 8, 0, 0);
        body.Children.Add(Error);

        var create = DialogKit.Action(L.T("Create"), primary: true);
        create.Click += (_, _) => { if ((Request = Accept()) != null) DialogResult = true; };
        DialogKit.Frame(this, "", DialogKit.Create, L.T("Create an alarm"),
            L.T("An alarm of OPC UA Part 9 beside the value: the document server raises it when the value crosses the limit or leaves its normal state."),
            body, null, create, DialogKit.Action(L.T("Cancel"), cancel: true));
        Loaded += (_, _) => (boolean ? (Control)MessageBox : LimitBox).Focus();
    }

    private AlarmKind Kind => KindState.IsChecked == true ? AlarmKind.State : KindLow.IsChecked == true ? AlarmKind.Low : AlarmKind.High;

    /// <summary>The name and text for the kind chosen, unless the user wrote their own.</summary>
    private void Propose()
    {
        _proposing = true;
        if (!_nameOwn) NameBox.Text = AlarmElements.SuggestName(_value, Kind);
        if (!_messageOwn) MessageBox.Text = Kind switch
        {
            AlarmKind.High => L.T("{0} too high", _value.Name),
            AlarmKind.Low => L.T("{0} too low", _value.Name),
            _ => L.T("{0} not normal", _value.Name),
        };
        _proposing = false;
    }

    /// <summary>The request when every field holds what it must; otherwise null, and the dialog says why.</summary>
    public AlarmRequest? Accept()
    {
        double? limit = null;
        if (Kind != AlarmKind.State)
        {
            try { limit = Convert.ToDouble(ValueEntry.Parse("Double", -1, LimitBox.Text), System.Globalization.CultureInfo.InvariantCulture); }
            catch (ValueEntryException ex)
            {
                DialogKit.ShowError(Error, ValueTexts.Describe(ex));
                return null;
            }
        }
        ushort severity;
        if (SeverityBox.SelectedIndex is >= 0 and < 3) severity = Severities[SeverityBox.SelectedIndex];
        else if (!ushort.TryParse(CustomSeverityBox.Text.Trim(), out severity) || severity is < 1 or > 1000)
        {
            DialogKit.ShowError(Error, L.T("A severity is 1 to 1000."));
            return null;
        }
        var name = NameBox.Text.Trim();
        if (name.Length == 0)
        {
            DialogKit.ShowError(Error, L.T("Give the alarm a name."));
            return null;
        }
        if (_value.CAEXParent is IInternalElementContainer parent && parent.InternalElement.Any(e => e.Name == name))
        {
            DialogKit.ShowError(Error, L.T("'{0}' is taken beside '{1}'.", name, _value.Name));
            return null;
        }
        Error.Text = "";
        return new AlarmRequest(Kind, limit, Kind == AlarmKind.State ? NormalTrue.IsChecked == true : null, severity, MessageBox.Text.Trim(), name);
    }

    private static StackPanel Row(params UIElement[] children)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var c in children) row.Children.Add(c);
        return row;
    }

    private static string PathOf(InternalElementType e)
    {
        var names = new List<string>();
        for (CAEXObject? o = e; o is InternalElementType or InstanceHierarchyType; o = o.CAEXParent as CAEXObject) names.Insert(0, o!.Name);
        return string.Join("/", names);
    }
}
