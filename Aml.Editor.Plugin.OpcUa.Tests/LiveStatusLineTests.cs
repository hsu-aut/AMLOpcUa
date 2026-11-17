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

    /// <summary>
    /// Editor probe all11c (2026-11-17): "'OpcUaServer': 65 node(s) … Press Ctrl+S to save"
    /// was replaced by "Live: now also 15 element(s)" after 1.7 s.
    /// </summary>
    [Fact]
    public void A_message_that_comes_on_its_own_waits_until_the_one_there_was_readable_five_seconds()
    {
        var line = new LiveStatusLine();
        line.Other(T0);
        var now = T0.AddSeconds(1.7);
        var waits = new List<TimeSpan>();

        line.WaitTillReadAsync(() => now, wait =>
        {
            waits.Add(wait);
            // While waiting, a command of the user says something too: that one is to be read as well.
            if (waits.Count == 1) line.Other(T0.AddSeconds(3));
            now += wait;
            return Task.CompletedTask;
        }).GetAwaiter().GetResult();

        Assert.Equal([TimeSpan.FromSeconds(3.3), TimeSpan.FromSeconds(3)], waits);
        Assert.Equal(T0.AddSeconds(8), now);
    }

    [Fact]
    public void A_message_that_comes_on_its_own_does_not_wait_when_the_line_was_read()
    {
        var line = new LiveStatusLine();
        line.Other(T0);
        var waited = false;

        line.WaitTillReadAsync(() => T0.AddSeconds(6), _ => { waited = true; return Task.CompletedTask; }).GetAwaiter().GetResult();

        Assert.False(waited);
    }
}
