using Aml.Editor.Plugin.OpcUa;

namespace PluginTests;

/// <summary>
/// While values are kept live, every value used to take the status line within a second:
/// "This variable can only be read" or "The watch has received no value yet" were gone
/// before anyone read them (seen in the editor probe of 2026-11-17).
/// </summary>
public class LiveStatusLineTests
{
    private static readonly DateTime T0 = new(2026, 11, 17, 11, 0, 0);

    [Fact]
    public void Live_values_take_the_line_at_most_once_a_second()
    {
        var line = new LiveStatusLine();

        Assert.True(line.MayShowLive(T0));
        Assert.False(line.MayShowLive(T0.AddMilliseconds(500)));
        Assert.True(line.MayShowLive(T0.AddMilliseconds(1100)));
    }

    [Fact]
    public void Another_message_stays_five_seconds_before_live_values_take_the_line_again()
    {
        var line = new LiveStatusLine();
        line.Other(T0);

        Assert.False(line.MayShowLive(T0.AddSeconds(1)));
        Assert.False(line.MayShowLive(T0.AddSeconds(4.9)));
        Assert.True(line.MayShowLive(T0.AddSeconds(5.1)));
    }
}
