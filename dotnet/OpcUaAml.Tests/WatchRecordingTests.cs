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

    private static UaReadResult Read(UaNodeAddress a, object? value, bool good = true, string status = "Good") =>
        new(a, value, value?.GetType().Name, good, status);

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

        var lines = recording.ToCsv().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);

        Assert.Equal("Time;Node;NodeId;Value;Unit;Status", lines[0]);
        Assert.Equal("2026-11-24T10:00:00.000+01:00;Speed;\"nsu=urn:plant;s=Drill/Speed\";1200.5;min⁻¹;Good", lines[1]);
        Assert.Equal("2026-11-24T10:00:00.250+01:00;Speed;\"nsu=urn:plant;s=Drill/Speed\";;min⁻¹;BadCommunicationError", lines[2]);
    }

    [Fact]
    public void A_text_with_a_semicolon_or_quote_is_quoted()
    {
        var recording = new WatchRecording();
        recording.Add(T0, "Label", "", Read(Running, "a;b \"c\""));

        Assert.Contains(";\"a;b \"\"c\"\"\";", recording.ToCsv());
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
