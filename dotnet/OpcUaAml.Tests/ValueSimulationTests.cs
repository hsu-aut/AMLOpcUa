using Opc.Ua;
using OpcUaAml.Server;

namespace OpcUaAml.Tests;

/// <summary>
/// How a simulated plant moves its values. Counters (a count of cycles or
/// of parts made) count up as in a running plant; other numbers swing.
/// </summary>
public class ValueSimulationTests
{
    private static List<object?> Over(NodeId dataType, object middle, string name, double phase = 1.4) =>
        Enumerable.Range(0, 121).Select(i => ValueSimulation.Next(dataType, middle, i * 0.5, phase, name)).ToList();

    [Theory]
    [InlineData("PartsProduced")]
    [InlineData("CycleCount")]
    public void A_counter_counts_up_from_its_value_and_never_down(string name)
    {
        var values = Over(DataTypeIds.Int32, 115, name).Cast<int>().ToList();

        Assert.Equal(115, values[0]);
        Assert.True(values[^1] > values[0], string.Join(",", values));
        Assert.All(values.Zip(values.Skip(1)), pair => Assert.True(pair.Second >= pair.First, $"{pair.First} then {pair.Second}"));
    }

    [Fact]
    public void An_unsigned_counter_counts_up_in_its_own_type()
    {
        var values = Over(DataTypeIds.UInt32, 302u, "CycleCount");

        Assert.All(values, v => Assert.IsType<uint>(v));
        Assert.True((uint)values[^1]! > 302u);
    }

    [Fact]
    public void Other_numbers_swing_around_their_value()
    {
        var values = Over(DataTypeIds.Double, 1080.0, "Speed").Cast<double>().ToList();

        Assert.Contains(values, v => v > 1080);
        Assert.Contains(values, v => v < 1080);
    }

    [Fact]
    public void The_revision_counter_of_a_device_does_not_count_up()
    {
        // DI's RevisionCounter counts revisions of the device, not cycles.
        var values = Over(DataTypeIds.Int32, 5, "RevisionCounter").Cast<int>().ToList();

        Assert.Contains(values, v => v < 5);
    }
}
