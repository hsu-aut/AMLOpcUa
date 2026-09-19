// What the user chose, kept across editor sessions in
// %APPDATA%\AMLOpcUa\settings.json. A missing or unreadable file means
// defaults; the plugin must work on a fresh machine without any setup.

using System.IO;
using System.Text.Json;

namespace Aml.Editor.Plugin.OpcUa.Diagnostics;

public sealed class PluginSettings
{
    /// <summary>Folders searched for NodeSets that an import requires.</summary>
    public List<string> NodeSetFolders { get; set; } = new();

    /// <summary>Folder of the last NodeSet opened, for the file dialog.</summary>
    public string? LastNodeSetFolder { get; set; }

    /// <summary>Trigger the editor's save after an import.</summary>
    public bool SaveAfterImport { get; set; }

    /// <summary>Replace OPC UA libraries the document already has.</summary>
    public bool ReplaceExistingLibraries { get; set; } = true;

    public bool DebugLogging { get; set; }

    /// <summary>The last OPC UA endpoint connected to.</summary>
    public string? LastEndpointUrl { get; set; }

    /// <summary>Endpoints connected to recently, newest first.</summary>
    public List<string> RecentEndpoints { get; set; } = new();

    /// <summary>The Cloud Library account last used; the password is never stored.</summary>
    public string? CloudLibraryUser { get; set; }

    /// <summary>Prefer a secured endpoint when connecting.</summary>
    public bool UseSecurity { get; set; } = true;

    /// <summary>Upper bound on nodes taken in one mirror.</summary>
    public int MirrorMaxNodes { get; set; } = OpcUaAml.Server.MirrorOptions.DefaultMaxNodes;

    /// <summary>What mirroring again does with elements whose node the server no longer has.</summary>
    public OpcUaAml.Server.VanishedNodes MirrorVanished { get; set; } = OpcUaAml.Server.VanishedNodes.Report;

    private static string FilePath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AMLOpcUa", "settings.json");

    public static PluginSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<PluginSettings>(File.ReadAllText(FilePath)) ?? new PluginSettings();
        }
        catch (Exception ex)
        {
            PluginLog.Warn($"Settings could not be read, using defaults: {ex.Message}");
        }
        return new PluginSettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex)
        {
            PluginLog.Warn($"Settings could not be saved: {ex.Message}");
        }
    }
}
