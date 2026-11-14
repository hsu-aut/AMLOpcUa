using System.IO;
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

    [Fact]
    public void The_help_on_values_says_what_bound_means()
    {
        // Reading and keeping values live work on "bound" elements; only the manual said what that is.
        var commands = HelpTopics.All["server.commands"];
        Assert.Contains("Binding", commands.Terms);
        var (_, text) = HelpTopics.Glossary["Binding"];
        Assert.Contains("NodeId", text);
        Assert.Contains("Value", text);
    }

    [Fact]
    public void Connecting_to_a_server_leads_to_the_practice_plant_lesson()
    {
        Assert.Contains("plant", Lessons.Titles.Keys);
        Assert.Equal("plant", HelpTopics.All["server.connection"].Lesson);
        Assert.Equal("plant", HelpTopics.All["server.commands"].Lesson);
        Assert.Equal("server", HelpTopics.All["server.serve"].Lesson);
    }

    [Fact]
    public void The_AutomationML_terms_an_OPC_UA_developer_meets_are_explained()
    {
        // The glossary explained OPC UA to people who know it and AutomationML to nobody.
        foreach (var term in new[] { "CAEX", "InstanceHierarchy", "InternalElement", "SystemUnitClass", "RoleClass", "InterfaceClass",
                                     "ExternalInterface", "InternalLink", "Attribute", "AttributeType", "Node" })
            Assert.True(HelpTopics.Glossary.ContainsKey(term), $"no explanation of {term}");
        // The one fact an AutomationML engineer cannot guess: a value of a server is an element with an
        // attribute Value, not an attribute. Language-neutral: the glossary is built in the language of the machine.
        Assert.Contains("InternalElement", HelpTopics.Glossary["InternalElement"].Text);
        Assert.Contains("Value", HelpTopics.Glossary["InternalElement"].Text);
        Assert.Contains("Value", HelpTopics.Glossary["Attribute"].Text);
        Assert.Contains("UA", HelpTopics.Glossary["Attribute"].Text);
    }

    [Fact]
    public void Every_explained_term_is_offered_by_a_topic()
    {
        var offered = HelpTopics.All.Values.SelectMany(t => t.Terms).ToHashSet();
        Assert.All(HelpTopics.Glossary.Keys, k => Assert.True(offered.Contains(k), $"{k} is explained, but no '?' offers it"));
    }

    /// <summary>The anchors GitHub gives the headings of docs/manual.md.</summary>
    private static HashSet<string> ManualAnchors()
    {
        var repository = new DirectoryInfo(AppContext.BaseDirectory);
        while (repository != null && !File.Exists(Path.Combine(repository.FullName, "AMLOpcUa.sln"))) repository = repository.Parent;
        var manual = File.ReadAllLines(Path.Combine(repository!.FullName, "docs", "manual.md"));
        return manual.Where(l => l.StartsWith('#'))
            .Select(l => l.TrimStart('#').Trim().ToLowerInvariant())
            .Select(h => new string(h.Where(c => char.IsLetterOrDigit(c) || c is ' ' or '-' or '_').ToArray()).Replace(' ', '-'))
            .ToHashSet();
    }

    [Fact]
    public void Every_help_topic_leads_to_its_section_of_the_manual()
    {
        // Who installs from the PlugIn Manager never sees the repository; the "?" is where they look.
        var anchors = ManualAnchors();
        foreach (var (key, topic) in HelpTopics.All)
        {
            Assert.False(string.IsNullOrEmpty(topic.Manual), $"{key}: no section of the manual");
            Assert.True(anchors.Contains(topic.Manual!), $"{key}: the manual has no section #{topic.Manual}");
        }
        Assert.EndsWith("docs/manual.md", Manual.Url);
        Assert.Equal(Manual.Url + "#connect", Manual.At("connect"));
    }

    [Fact]
    public void The_manual_is_one_click_away_on_every_tab()
    {
        WithPlugin(plugin =>
        {
            var link = Assert.IsType<System.Windows.Controls.Button>(plugin.FindName("ManualLink"));
            Assert.IsType<System.Windows.Controls.Primitives.StatusBarItem>(LogicalTreeHelper.GetParent(link));
        });
        // German readers start with the short guide in German, which leads into the manual.
        Assert.EndsWith("docs/manual.md", Manual.Start(german: false));
        Assert.EndsWith("docs/kurzanleitung.md", Manual.Start(german: true));
    }

    [Fact]
    public void Every_named_button_and_every_field_has_a_tooltip()
    {
        var silent = new List<string>();
        WithPlugin(plugin =>
        {
            foreach (var c in Descendants<System.Windows.Controls.Control>(plugin))
            {
                if (c is not (System.Windows.Controls.Button or System.Windows.Controls.TextBox or System.Windows.Controls.ComboBox
                    or System.Windows.Controls.PasswordBox or System.Windows.Controls.CheckBox)) continue;
                if (c is System.Windows.Controls.Button && string.IsNullOrEmpty(c.Name)) continue;   // cards and dialog buttons carry their text
                if (c is HelpButton) continue;   // takes its tooltip from its topic when loaded
                if (c.ToolTip == null) silent.Add($"{c.GetType().Name} {c.Name}");
            }
        });
        Assert.Empty(silent);
    }
}
