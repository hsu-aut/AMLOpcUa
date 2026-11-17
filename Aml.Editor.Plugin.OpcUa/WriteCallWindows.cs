// Write value… and Call method…: what to type for a variable or each argument
// of a method, checked before anything goes to the server, and the server's
// answer. Both act on a running plant, so both say so and ask once.

using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Aml.Editor.Plugin.OpcUa.Diagnostics;
using Aml.Editor.Plugin.OpcUa.Localization;
using OpcUaAml.Server;

namespace Aml.Editor.Plugin.OpcUa;

/// <summary>Values typed by the user: what to type, and what was wrong, in the user's language.</summary>
public static class ValueTexts
{
    public static string Describe(ValueEntryException ex)
    {
        var text = ex.Problem switch
        {
            ValueProblem.NotBoolean => L.T("'{0}' is not a Boolean: write true or false.", ex.Text),
            ValueProblem.NotWholeNumber => L.T("'{0}' is not a whole number.", ex.Text),
            ValueProblem.OutOfRange => L.T("'{0}' is out of range: a {1} is {2}.", ex.Text, ex.Type, ex.Range ?? ""),
            ValueProblem.NotNumber => L.T("'{0}' is not a number: write 1.5, for instance.", ex.Text),
            ValueProblem.NotTime => L.T("'{0}' is not a time: write 2026-11-24T10:00:00Z, for instance.", ex.Text),
            ValueProblem.NotGuid => L.T("'{0}' is not a GUID.", ex.Text),
            ValueProblem.NotBase64 => L.T("'{0}' is not Base64.", ex.Text),
            ValueProblem.Matrix => L.T("Values with {0} dimensions cannot be typed in.", ex.Range ?? ""),
            _ => L.T("Values of the type {0} cannot be typed in.", ex.Type),
        };
        return ex.Argument != null ? ex.Argument + ": " + text : text;
    }

    /// <summary>What to type for a value of the built-in type and value rank.</summary>
    public static string Hint(string type, int valueRank)
    {
        var array = valueRank is 0 or 1;
        return type switch
        {
            "Boolean" => array ? L.T("true or false, separated by spaces") : L.T("true or false"),
            "SByte" or "Byte" or "Int16" or "UInt16" or "Int32" or "UInt32" or "Int64" or "UInt64" =>
                array ? L.T("whole numbers, separated by spaces") : L.T("a whole number"),
            "Float" or "Double" => array ? L.T("numbers, separated by spaces, such as 1.5 2") : L.T("a number, such as 1.5"),
            "Enumeration" => array ? L.T("numbers of values, separated by spaces") : L.T("the number of a value"),
            "String" or "LocalizedText" => array ? L.T("words, separated by spaces") : L.T("any text"),
            "DateTime" => L.T("a time, such as 2026-11-24T10:00:00Z"),
            "Guid" => L.T("a GUID"),
            "ByteString" => L.T("Base64"),
            _ => L.T("a value of this type cannot be typed in"),
        };
    }
}

/// <summary>The dialog of Write value….</summary>
public sealed class WriteValueWindow : Window
{
    private readonly UaVariableInfo _info;

    public TextBox Input { get; } = new() { MinWidth = 320 };
    public TextBlock Error { get; } = DialogKit.Message();

    public WriteValueWindow(string node, string address, UaVariableInfo info, string current)
    {
        _info = info;
        Width = 560;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        Input.Text = current;
        AutomationProperties.SetName(Input, L.T("New value"));

        var body = new StackPanel();
        body.Children.Add(DialogKit.Facts(
            (L.T("Node"), node, false),
            (L.T("NodeId"), address, true),
            (L.T("DataType"), info.DataType == info.BuiltInType ? info.DataType : $"{info.DataType} ({info.BuiltInType})", false),
            (L.T("Value now"), current, true)));
        body.Children.Add(DialogKit.Label(L.T("New value")));
        body.Children.Add(Input);
        body.Children.Add(new TextBlock { Text = ValueTexts.Hint(info.BuiltInType, info.ValueRank), Foreground = DialogKit.Muted, Margin = new Thickness(0, 3, 0, 0) });
        Error.Margin = new Thickness(0, 8, 0, 0);
        body.Children.Add(Error);

        var write = DialogKit.Action(L.T("Write"), primary: true);
        write.Click += (_, _) => { if (Accept()) DialogResult = true; };
        DialogKit.Frame(this, "", DialogKit.Danger, L.T("Write a value into the server"),
            L.T("The server takes the value at once; a PLC may act on it. Write only into a plant you may change."),
            body, null, write, DialogKit.Action(L.T("Cancel"), cancel: true));
        Loaded += (_, _) => { Input.Focus(); Input.SelectAll(); };
    }

    /// <summary>True when the text is a value of the variable's type; otherwise says why.</summary>
    public bool Accept()
    {
        try
        {
            ValueEntry.Parse(_info.BuiltInType, _info.ValueRank, Input.Text);
            Error.Text = "";
            return true;
        }
        catch (ValueEntryException ex)
        {
            DialogKit.ShowError(Error, ValueTexts.Describe(ex));
            return false;
        }
    }
}

/// <summary>The dialog of Call method…: a field per input argument, the server's answer below.</summary>
public sealed class CallMethodWindow : Window
{
    private readonly UaMethodInfo _info;
    private readonly Func<IReadOnlyList<string>, Task<UaCallResult>> _call;
    private readonly Button _go;

    public IReadOnlyList<TextBox> Inputs { get; }
    public TextBlock Result { get; } = new() { TextWrapping = TextWrapping.Wrap, FontFamily = new System.Windows.Media.FontFamily("Consolas"), Margin = new Thickness(0, 10, 0, 0) };

    public CallMethodWindow(string method, string target, UaMethodInfo info, Func<IReadOnlyList<string>, Task<UaCallResult>> call)
    {
        _info = info;
        _call = call;
        Width = 600;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;

        var body = new StackPanel();
        body.Children.Add(DialogKit.Facts((L.T("Method"), method, false), (L.T("Called on"), target, false)));
        if (!info.Executable)
            body.Children.Add(new TextBlock { Text = L.T("The server says this method may not be called now."), Foreground = DialogKit.Danger, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) });
        var inputs = new List<TextBox>();
        if (info.Inputs.Count == 0) body.Children.Add(DialogKit.Label(L.T("The method takes no arguments.")));
        foreach (var a in info.Inputs)
        {
            body.Children.Add(DialogKit.Label($"{a.Name}   ({a.DataType})"));
            var box = new TextBox { MinWidth = 320, ToolTip = a.Description.Length > 0 ? a.Description : null };
            AutomationProperties.SetName(box, a.Name);
            if (a.Description.Length > 0) AutomationProperties.SetHelpText(box, a.Description);
            body.Children.Add(box);
            body.Children.Add(new TextBlock { Text = ValueTexts.Hint(a.BuiltInType, a.ValueRank) + (a.Description.Length > 0 ? "  ·  " + a.Description : ""), Foreground = DialogKit.Muted, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 3, 0, 0) });
            inputs.Add(box);
        }
        Inputs = inputs;
        // A name of its own would hide the answer from a screen reader: the
        // answer is the name, what it is goes into the help text.
        AutomationProperties.SetHelpText(Result, L.T("Answer of the server"));
        AutomationProperties.SetLiveSetting(Result, AutomationLiveSetting.Polite);
        body.Children.Add(Result);

        _go = DialogKit.Action(L.T("Call"), primary: true);
        _go.Click += async (_, _) => await CallAsync();
        DialogKit.Frame(this, "", DialogKit.Danger, L.T("Call a method of the server"),
            L.T("The server runs the method at once; on a PLC it may move the plant. Call only in a plant you may change."),
            body, null, _go, DialogKit.Action(L.T("Close"), cancel: true));
        Loaded += (_, _) => { if (Inputs.Count > 0) Inputs[0].Focus(); };
    }

    /// <summary>Checks the arguments, calls, and shows the answer; the dialog stays open for another call.</summary>
    public async Task CallAsync()
    {
        var texts = Inputs.Select(i => i.Text).ToList();
        for (var i = 0; i < texts.Count; i++)
        {
            try { ValueEntry.Parse(_info.Inputs[i].BuiltInType, _info.Inputs[i].ValueRank, texts[i]); }
            catch (ValueEntryException ex)
            {
                DialogKit.ShowError(Result, ValueTexts.Describe(ex.For(_info.Inputs[i].Name)));
                Announce();
                Inputs[i].Focus();
                return;
            }
        }
        _go.IsEnabled = false;
        Result.ClearValue(TextBlock.ForegroundProperty);
        Result.Text = L.T("Calling …");
        try
        {
            var answer = await _call(texts);
            var lines = new List<string> { answer.Good ? L.T("Done: {0}", answer.Status) : L.T("The server refused: {0}", answer.Status) };
            lines.AddRange(answer.Outputs.Select(o => $"{o.Name} = {o.Value}"));
            Result.Text = string.Join(Environment.NewLine, lines);
            if (!answer.Good) Result.Foreground = DialogKit.Danger;
        }
        catch (ValueEntryException ex) { DialogKit.ShowError(Result, ValueTexts.Describe(ex)); }
        catch (Exception ex)
        {
            PluginLog.Error("Calling a method failed", ex);
            DialogKit.ShowError(Result, L.T("Calling failed: {0}", ex.Message));
        }
        finally
        {
            _go.IsEnabled = true;
            Announce();
        }
    }

    /// <summary>
    /// A live region is read out only when it says it changed; WPF does not
    /// do that by itself. Without a screen reader there is no peer, and nothing to do.
    /// </summary>
    private void Announce() =>
        System.Windows.Automation.Peers.UIElementAutomationPeer.FromElement(Result)?
            .RaiseAutomationEvent(System.Windows.Automation.Peers.AutomationEvents.LiveRegionChanged);
}
