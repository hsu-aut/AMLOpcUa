using System.Windows;
using System.Windows.Media;
using Aml.Editor.Plugin.OpcUa;
using Aml.Editor.Plugin.OpcUa.Guide;

namespace PluginTests;

/// <summary>
/// The tutorial and the "?" buttons name controls and topics by string; a
/// renamed control or a missing topic would break them silently. These tests
/// break instead.
/// </summary>
public class GuideTests
{
    /// <summary>Runs <paramref name="test"/> with a plugin view on a thread WPF accepts.</summary>
    private static void WithPlugin(Action<OpcUaPlugin> test)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { test(new OpcUaPlugin()); }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure != null) throw new Xunit.Sdk.XunitException(failure.ToString());
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
        {
            if (child is T t) yield return t;
            foreach (var d in Descendants<T>(child)) yield return d;
        }
    }

    [Fact]
    public void Every_step_of_every_lesson_names_a_control_and_a_tab_that_exist()
    {
        WithPlugin(plugin =>
        {
            var lessons = plugin.TourLessons();
            Assert.Equal(Lessons.Titles.Keys.OrderBy(k => k), lessons.Select(l => l.Id).OrderBy(k => k));
            foreach (var lesson in lessons)
            foreach (var step in lesson.Steps)
            {
                if (step.Target != null) Assert.True(plugin.FindName(step.Target) is FrameworkElement, $"{lesson.Id}: no control {step.Target}");
                if (step.Tab != null) Assert.True(plugin.FindName(step.Tab) is System.Windows.Controls.TabItem, $"{lesson.Id}: no tab {step.Tab}");
            }
        });
    }

    [Fact]
    public void Every_help_button_has_its_topic_and_every_term_its_explanation()
    {
        WithPlugin(plugin =>
        {
            var buttons = Descendants<HelpButton>(plugin).ToList();
            Assert.True(buttons.Count >= HelpTopics.All.Count, $"{buttons.Count} help buttons for {HelpTopics.All.Count} topics");
            Assert.All(buttons, b => Assert.True(HelpTopics.All.ContainsKey(b.Topic), $"no topic {b.Topic}"));
            Assert.Equal(HelpTopics.All.Keys.OrderBy(k => k), buttons.Select(b => b.Topic).Distinct().OrderBy(k => k));
        });
        foreach (var topic in HelpTopics.All.Values)
        {
            Assert.All(topic.Terms, t => Assert.True(HelpTopics.Glossary.ContainsKey(t), $"{topic.Title}: no explanation of {t}"));
            if (topic.Lesson != null) Assert.True(Lessons.Titles.ContainsKey(topic.Lesson), $"{topic.Title}: no lesson {topic.Lesson}");
        }
    }
}
