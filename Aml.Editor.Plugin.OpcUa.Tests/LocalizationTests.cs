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
    public void The_German_writes_no_dash_where_it_would_pause()
    {
        Assert.DoesNotContain(De.Texts.Values, v => Regex.IsMatch(v, " [-–—] ") || v.Contains('—'));
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
