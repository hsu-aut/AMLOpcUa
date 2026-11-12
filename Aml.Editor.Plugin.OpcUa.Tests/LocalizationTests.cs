using System.IO;
using System.Text.RegularExpressions;
using Aml.Editor.Plugin.OpcUa.Localization;

namespace PluginTests;

/// <summary>
/// The plugin speaks English or German. Every text it shows is looked up by its
/// English, in code as L.T("…") and in XAML as {l:T '…'}; these tests keep the
/// German entries complete and free of leftovers.
/// </summary>
public class LocalizationTests
{
    private static string PluginFolder()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "AMLOpcUa.sln")))
                return Path.Combine(dir.FullName, "Aml.Editor.Plugin.OpcUa");
        throw new DirectoryNotFoundException("AMLOpcUa.sln was not found above the test folder.");
    }

    private static HashSet<string> Keys()
    {
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var file in Directory.EnumerateFiles(PluginFolder(), "*.*", SearchOption.AllDirectories)
                     .Where(f => (f.EndsWith(".cs") || f.EndsWith(".xaml")) && !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                                 && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}") && !f.EndsWith("De.cs")))
        {
            var source = File.ReadAllText(file);
            foreach (Match m in Regex.Matches(source, @"\bL\.T\(\s*""((?:[^""\\]|\\.)*)"""))
                keys.Add(Regex.Unescape(m.Groups[1].Value));
            foreach (Match m in Regex.Matches(source, @"\{l:T\s+'((?:[^'\\]|\\.)*)'\s*\}"))
                keys.Add(m.Groups[1].Value.Replace("\\'", "'").Replace("&amp;", "&").Replace("&quot;", "\"").Replace("&lt;", "<").Replace("&gt;", ">"));
        }
        // The check's rule descriptions come from the core and are looked up by their English.
        foreach (var description in OpcUaAml.Checks.Rules.Descriptions.Values) keys.Add(description);
        return keys;
    }

    [Fact]
    public void Every_text_has_a_German_entry_and_none_is_left_over()
    {
        var keys = Keys();

        Assert.NotEmpty(keys);
        Assert.Empty(keys.Where(k => !De.Texts.ContainsKey(k)).Order());
        Assert.Empty(De.Texts.Keys.Where(k => !keys.Contains(k)).Order());
    }

    [Fact]
    public void No_status_text_goes_past_the_translation()
    {
        var bypassing = Directory.EnumerateFiles(PluginFolder(), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"))
            .SelectMany(f => File.ReadAllLines(f).Select(line => (File: Path.GetFileName(f), Line: line.Trim())))
            // Guard names the command in "… failed: …"; seen in German as
            // "Adding the Mandatory children fehlgeschlagen".
            .Where(l => Regex.IsMatch(l.Line, @"\b(SetStatus|SetBusy|Guard)\((\w+,\s*)?\$?""[^""]"))
            .Select(l => $"{l.File}: {l.Line}");

        Assert.Empty(bypassing);
    }

    [Fact]
    public void The_German_writes_no_dash_where_it_would_pause()
    {
        Assert.DoesNotContain(De.Texts.Values, v => Regex.IsMatch(v, " [-–—] ") || v.Contains('—'));
    }

    /// <summary>
    /// The German says "du" throughout; two texts said "Sie", one of them the
    /// first thing shown when the practice plant starts.
    /// </summary>
    [Fact]
    public void The_German_addresses_the_reader_with_du_only()
    {
        // "Sie" for a feminine noun before it (die Übungsanlage) stays; the formal
        // address is "Sie" after a verb or with "Ihr…".
        var formal = new Regex(@"\b[A-ZÄÖÜ][a-zäöüß]+en Sie\b|\b(Ihnen|Ihr|Ihre|Ihren|Ihrem|Ihrer|Ihres)\b");
        Assert.Empty(De.Texts.Values.Where(v => formal.IsMatch(v)));
    }

    [Fact]
    public void The_language_is_Windows_and_not_what_the_host_process_sets()
    {
        // The AML Editor sets the UI culture of its whole process to English.
        var before = System.Globalization.CultureInfo.CurrentUICulture;
        try
        {
            System.Globalization.CultureInfo.CurrentUICulture = new System.Globalization.CultureInfo("en");
            var underEnglish = L.WindowsLanguage().Name;
            System.Globalization.CultureInfo.CurrentUICulture = new System.Globalization.CultureInfo("fr");
            var underFrench = L.WindowsLanguage().Name;

            Assert.Equal(underEnglish, underFrench);
        }
        finally
        {
            System.Globalization.CultureInfo.CurrentUICulture = before;
        }
    }

    [Theory]
    [InlineData(null, "de", true)]
    [InlineData(null, "en", false)]
    [InlineData(null, "fr", false)]
    [InlineData("en", "de", false)]
    [InlineData("EN", "de", false)]
    [InlineData("de", "en", true)]
    [InlineData("", "de", true)]
    [InlineData("xx", "de", true)]
    public void AMLOPCUA_LANGUAGE_overrides_the_language_of_Windows(string? setting, string windows, bool german)
    {
        Assert.Equal(german, L.IsGerman(setting, new System.Globalization.CultureInfo(windows)));
    }

    /// <summary>
    /// The language chosen under Settings › Language, between the environment
    /// variable (for support) and Windows. Before, only the variable could
    /// choose, which a user at a booth does not set.
    /// </summary>
    [Theory]
    [InlineData(null, "en", "de", false)]
    [InlineData(null, "de", "en", true)]
    [InlineData(null, null, "de", true)]
    [InlineData("de", "en", "en", true)]
    [InlineData("en", "de", "de", false)]
    public void The_language_of_the_settings_comes_before_that_of_Windows(string? variable, string? chosen, string windows, bool german)
    {
        Assert.Equal(german, L.IsGerman(variable, chosen, new System.Globalization.CultureInfo(windows)));
    }

    [Fact]
    public void The_chosen_language_is_kept_in_the_settings()
    {
        var settings = Aml.Editor.Plugin.OpcUa.Diagnostics.PluginSettings.Load(out _);
        var before = settings.Language;
        try
        {
            settings.Language = "en";
            settings.Save();

            Assert.Equal("en", Aml.Editor.Plugin.OpcUa.Diagnostics.PluginSettings.Load(out _).Language);
        }
        finally
        {
            settings.Language = before;
            settings.Save();
        }
    }

    [Fact]
    public void Taking_instances_is_reported_in_the_plugins_language()
    {
        var german = L.German;
        try
        {
            var result = new OpcUaAml.Import.InstanceTransferResult(1, 274, 2, "IH_MPS500");
            L.German = false;
            Assert.Equal("1 instance(s) with 274 element(s) taken into IH_MPS500, 2 link(s) to nodes outside left out.", Summaries.Transfer(result));
            L.German = true;
            Assert.Equal("1 Instanz(en) mit 274 Element(en) in IH_MPS500 übernommen, 2 Verbindung(en) zu Knoten außerhalb ausgelassen.", Summaries.Transfer(result));
        }
        finally
        {
            L.German = german;
        }
    }

    [Fact]
    public void A_text_is_German_when_Windows_is_and_keeps_its_values()
    {
        var german = L.German;
        try
        {
            L.German = false;
            Assert.Equal("Count nodes", L.T("Count nodes"));
            L.German = true;
            Assert.Equal("Knoten zählen", L.T("Count nodes"));
            Assert.Equal("Prüfen (3)", L.T("Check ({0})", 3));
        }
        finally
        {
            L.German = german;
        }
    }
}
