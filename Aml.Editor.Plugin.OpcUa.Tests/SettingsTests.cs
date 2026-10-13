using System.IO;
using System.Runtime.CompilerServices;
using Aml.Editor.Plugin.OpcUa;
using Aml.Editor.Plugin.OpcUa.Diagnostics;

namespace PluginTests;

/// <summary>Every plugin a test creates keeps its settings in a folder of the tests, not in the user's.</summary>
internal static class TestSettingsFolder
{
    [ModuleInitializer]
    internal static void Redirect() =>
        PluginSettings.Folder = Directory.CreateTempSubdirectory("opcuaaml-plugin-settings-").FullName;
}

public class SettingsTests
{
    private static void OnUiThread(Action test)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { test(); }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure != null) throw new Xunit.Sdk.XunitException(failure.ToString());
    }

    [Fact]
    public void The_tests_do_not_touch_the_users_settings()
    {
        var users = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AMLOpcUa");
        Assert.NotEqual(users, PluginSettings.Folder);
    }

    [Fact]
    public void Opening_the_plugin_keeps_save_after_import()
    {
        new PluginSettings { SaveAfterImport = true, ReplaceExistingLibraries = true }.Save();

        OnUiThread(() => _ = new OpcUaPlugin());

        var after = PluginSettings.Load(out var problem);
        Assert.Null(problem);
        Assert.True(after.SaveAfterImport);
        Assert.True(after.ReplaceExistingLibraries);
    }
}
