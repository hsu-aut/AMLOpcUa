using Aml.Editor.Plugin.OpcUa;
using Aml.Editor.Plugin.OpcUa.Localization;
using OpcUaAml.Server;

namespace PluginTests;

/// <summary>
/// Write value… and Call method…: the dialogs say what to type, refuse what
/// is no value of the type in the user's language, and a call shows the
/// server's answer with the outputs.
/// </summary>
public class WriteCallDialogTests
{
    private static T OnUiThread<T>(Func<T> work)
    {
        T result = default!;
        Exception? failure = null;
        var german = L.German;
        L.German = false;
        var thread = new Thread(() =>
        {
            try { result = work(); }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        L.German = german;
        if (failure != null) throw new Xunit.Sdk.XunitException(failure.ToString());
        return result;
    }

    /// <summary>What a message line shows: its runs, as a problem is shown with a glyph before it.</summary>
    private static string Shown(System.Windows.Controls.TextBlock block) =>
        string.Concat(block.Inlines.OfType<System.Windows.Documents.Run>().Select(r => r.Text));

    [Theory]
    [InlineData(ValueProblem.NotWholeNumber, "abc", "Int32", null, null, "'abc' is not a whole number.")]
    [InlineData(ValueProblem.OutOfRange, "300", "Byte", "0 to 255", null, "'300' is out of range: a Byte is 0 to 255.")]
    [InlineData(ValueProblem.NotNumber, "x", "Double", null, "Factor", "Factor: 'x' is not a number: write 1.5, for instance.")]
    [InlineData(ValueProblem.NotTypeable, "x", "ExtensionObject", null, null, "Values of the type ExtensionObject cannot be typed in.")]
    public void A_text_that_is_no_value_is_told_in_the_users_language(ValueProblem problem, string text, string type, string? range, string? argument, string expected) =>
        Assert.Equal(expected, OnUiThread(() => ValueTexts.Describe(new ValueEntryException(problem, text, type, range, argument))));

    // Review of 2026-11-18: "'300' liegt außerhalb: ein Byte reicht von 0 to 255."
    [Fact]
    public void The_range_is_told_in_German_without_English_in_it()
    {
        var german = L.German;
        L.German = true;
        try
        {
            var text = ValueTexts.Describe(new ValueEntryException(ValueProblem.OutOfRange, "300", "Byte", "0 to 255", min: "0", max: "255"));
            Assert.Contains("0 bis 255", text);
            Assert.DoesNotContain(" to ", text);
        }
        finally { L.German = german; }
    }

    [Fact]
    public void An_open_quote_is_told() =>
        Assert.Equal("'\"Line 1': a quote is not closed.", OnUiThread(() => ValueTexts.Describe(new ValueEntryException(ValueProblem.OpenQuote, "\"Line 1", "String"))));

    [Theory]
    [InlineData("String", 1, "quotes")]
    [InlineData("Boolean", -1, "true or false")]
    [InlineData("Double", -1, "a number, such as 1.5")]
    [InlineData("Int32", 1, "whole numbers, separated by spaces")]
    [InlineData("ExtensionObject", -1, "cannot be typed in")]
    public void The_dialog_says_what_to_type(string type, int rank, string hint) =>
        Assert.Contains(hint, OnUiThread(() => ValueTexts.Hint(type, rank)));

    [Fact]
    public void The_value_to_write_is_checked_before_the_dialog_closes()
    {
        var (refused, error, accepted) = OnUiThread(() =>
        {
            var window = new WriteValueWindow("Speed", "nsu=urn:x;i=1", new UaVariableInfo("Double", "Double", -1, true), "1.5");
            window.Input.Text = "fast";
            var refused = window.Accept();
            var error = Shown(window.Error);
            window.Input.Text = "2,5";
            return (refused, error, window.Accept() ? window.Input.Text : null);
        });

        Assert.False(refused);
        Assert.EndsWith("'fast' is not a number: write 1.5, for instance.", error);
        Assert.Equal("2,5", accepted);
    }

    [Fact]
    public void A_call_shows_the_status_and_the_outputs_of_the_server()
    {
        var info = new UaMethodInfo(
            [new UaArgument("Factor", "Double", "Double", -1, "How much to scale by")],
            [new UaArgument("Result", "Double", "Double", -1, "")],
            true);
        IReadOnlyList<string>? passed = null;
        var (badInput, shown) = OnUiThread(() =>
        {
            var window = new CallMethodWindow("Scale", "Machine", info, inputs =>
            {
                passed = inputs;
                return Task.FromResult(new UaCallResult(true, "Good", [("Result", "3")]));
            });
            window.Inputs[0].Text = "twice";
            window.CallAsync().GetAwaiter().GetResult();
            var badInput = Shown(window.Result);
            window.Inputs[0].Text = "1,5";
            window.CallAsync().GetAwaiter().GetResult();
            return (badInput, Shown(window.Result));
        });

        Assert.EndsWith("Factor: 'twice' is not a number: write 1.5, for instance.", badInput);
        Assert.Equal(["1,5"], passed);
        Assert.Contains("Result = 3", shown);
        Assert.Contains("Good", shown);
    }

    [Fact]
    public void A_screen_reader_reads_the_answer_itself_not_a_fixed_name()
    {
        var info = new UaMethodInfo([], [new UaArgument("Result", "Double", "Double", -1, "")], true);
        var name = OnUiThread(() =>
        {
            var window = new CallMethodWindow("Scale", "Machine", info,
                _ => Task.FromResult(new UaCallResult(true, "Good", [("Result", "3")])));
            window.CallAsync().GetAwaiter().GetResult();
            return new System.Windows.Automation.Peers.TextBlockAutomationPeer(window.Result).GetName();
        });

        Assert.Contains("Result = 3", name);
    }
}
