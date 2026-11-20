using System.Globalization;
using OpcUaAml.Addressing;
using OpcUaAml.Server;

namespace OpcUaAml.Tests;

/// <summary>
/// The values a watch received, kept with the time each arrived, for a CSV
/// file to open in a spreadsheet: what a variable did while it was watched.
/// </summary>
public class WatchRecordingTests
{
    private static readonly UaNodeAddress Speed = new("urn:plant", UaIdType.String, "Drill/Speed");
    private static readonly UaNodeAddress Running = new("urn:plant", UaIdType.String, "Drill/Running");
    private static readonly DateTimeOffset T0 = new(2026, 11, 24, 10, 0, 0, TimeSpan.FromHours(1));

    /// <summary>Semicolons and a decimal point, the file as it was before it followed the user's culture.</summary>
    private static readonly CultureInfo Invariant = Semicolons();

    private static CultureInfo Semicolons()
    {
        var culture = (CultureInfo)CultureInfo.InvariantCulture.Clone();
        culture.TextInfo.ListSeparator = ";";
        return culture;
    }

    private static UaReadResult Read(UaNodeAddress a, object? value, bool good = true, string status = "Good") =>
        new(a, value, value?.GetType().Name, good, status);

    /// <summary>
    /// Video round 2026-11-20: watching one more variable starts the watch again,
    /// and the server sends the current value of every watched variable once
    /// more. Each stood in the CSV once more, the first one four times after
    /// four variables were added.
    /// </summary>
    [Fact]
    public void A_value_sent_again_after_a_restart_is_kept_once()
    {
        var recording = new WatchRecording();
        recording.Add(T0, "Speed", "", Read(Speed, 1200.5));

        recording.Restarted();
        recording.Add(T0.AddSeconds(5), "Speed", "", Read(Speed, 1200.5));
        recording.Add(T0.AddSeconds(5), "Running", "", Read(Running, true));

        Assert.Equal(["1200.5", "true"], recording.Entries.Select(e => e.Value));
    }

    [Fact]
    public void After_a_restart_a_changed_value_and_later_repeats_are_kept()
    {
        var recording = new WatchRecording();
        recording.Add(T0, "Speed", "", Read(Speed, 1200.5));
        recording.Add(T0, "Running", "", Read(Running, true));

        recording.Restarted();
        recording.Add(T0.AddSeconds(5), "Speed", "", Read(Speed, 1300.0));
        recording.Add(T0.AddSeconds(5), "Running", "", Read(Running, true));
        // The server sent it on its own: the same value again is kept.
        recording.Add(T0.AddSeconds(6), "Running", "", Read(Running, true));

        Assert.Equal(["1200.5", "true", "1300", "true"], recording.Entries.Select(e => e.Value));
    }

    [Fact]
    public void Every_value_received_is_kept_with_its_time()
    {
        var recording = new WatchRecording();

        recording.Add(T0, "Speed", "min⁻¹", Read(Speed, 1200.5));
        recording.Add(T0.AddSeconds(1), "Running", "", Read(Running, true));
        recording.Add(T0.AddSeconds(2), "Speed", "min⁻¹", Read(Speed, 1210.0));

        Assert.Equal(3, recording.Count);
        Assert.Equal(["1200.5", "true", "1210"], recording.Entries.Select(e => e.Value));
    }

    [Fact]
    public void The_CSV_has_a_row_per_value_with_semicolons_and_ISO_times()
    {
        var recording = new WatchRecording();
        recording.Add(T0, "Speed", "min⁻¹", Read(Speed, 1200.5));
        recording.Add(T0.AddMilliseconds(250), "Speed", "min⁻¹", Read(Speed, null, good: false, status: "BadCommunicationError"));

        var lines = recording.ToCsv(Invariant).Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);

        Assert.Equal("Time;Node;NodeId;Value;Unit;Status", lines[0]);
        Assert.Equal("2026-11-24T10:00:00.000+01:00;Speed;\"nsu=urn:plant;s=Drill/Speed\";1200.5;min⁻¹;Good", lines[1]);
        Assert.Equal("2026-11-24T10:00:00.250+01:00;Speed;\"nsu=urn:plant;s=Drill/Speed\";;min⁻¹;BadCommunicationError", lines[2]);
    }

    [Fact]
    public void A_text_with_a_semicolon_or_quote_is_quoted()
    {
        var recording = new WatchRecording();
        recording.Add(T0, "Label", "", Read(Running, "a;b \"c\""));

        Assert.Contains(";\"a;b \"\"c\"\"\";", recording.ToCsv(Invariant));
    }

    // Review of 2026-11-18: in a German spreadsheet 1.5 became the 1st of May, and
    // a semicolon file opened in one column where the list separator is a comma.
    [Theory]
    [InlineData("de-DE", "2026-11-24T10:00:00.000+01:00;Speed;\"nsu=urn:plant;s=Drill/Speed\";1200,5;min⁻¹;Good")]
    [InlineData("en-US", "2026-11-24T10:00:00.000+01:00,Speed,nsu=urn:plant;s=Drill/Speed,1200.5,min⁻¹,Good")]
    public void The_CSV_takes_the_list_separator_and_decimal_sign_of_the_spreadsheet(string culture, string row)
    {
        var recording = new WatchRecording();
        recording.Add(T0, "Speed", "min⁻¹", Read(Speed, 1200.5));

        Assert.Equal(row, recording.ToCsv(CultureInfo.GetCultureInfo(culture)).Split(Environment.NewLine)[1]);
    }

    // A text from the server that starts like a formula ran as one.
    [Theory]
    [InlineData("=1+1")]
    [InlineData("+SUM(A1)")]
    [InlineData("-2+3")]
    [InlineData("@cmd")]
    public void A_text_that_starts_like_a_formula_stays_text(string text)
    {
        var recording = new WatchRecording();
        recording.Add(T0, text, "", Read(Running, text));

        // The node's name and the value: two cells with the apostrophe.
        var row = recording.ToCsv(Invariant).Split(Environment.NewLine)[1];
        Assert.Equal(2, row.Split(";'" + text + ";").Length - 1);
    }

    [Fact]
    public void A_negative_number_stays_a_number()
    {
        var recording = new WatchRecording();
        recording.Add(T0, "Speed", "", Read(Speed, -2.5));

        Assert.EndsWith(";-2.5;;Good", recording.ToCsv(Invariant).Split(Environment.NewLine)[1]);
    }

    // An Uncertain value (out of the engineering range, say) was saved without its value.
    [Fact]
    public void An_uncertain_value_is_kept_with_its_status()
    {
        var recording = new WatchRecording();
        recording.Add(T0, "Speed", "", Read(Speed, 101.5, good: false, status: "UncertainEngineeringUnitsExceeded"));

        Assert.Equal(("101.5", "UncertainEngineeringUnitsExceeded"), (recording.Entries[0].Value, recording.Entries[0].Status));
    }

    [Fact]
    public void Beyond_its_limit_the_oldest_values_go_first()
    {
        var recording = new WatchRecording(limit: 3);
        for (var i = 0; i < 5; i++) recording.Add(T0.AddSeconds(i), "Speed", "", Read(Speed, (double)i));

        Assert.Equal(["2", "3", "4"], recording.Entries.Select(e => e.Value));
        Assert.Equal(2, recording.Dropped);
    }

    [Fact]
    public void Clearing_starts_anew()
    {
        var recording = new WatchRecording(limit: 2);
        for (var i = 0; i < 3; i++) recording.Add(T0.AddSeconds(i), "Speed", "", Read(Speed, (double)i));

        recording.Clear();

        Assert.Equal(0, recording.Count);
        Assert.Equal(0, recording.Dropped);
    }
}
