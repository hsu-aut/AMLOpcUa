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

    /// <summary>The tutorial's lessons done, by id.</summary>
    public List<string> CompletedLessons { get; set; } = new();

    private static string FilePath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AMLOpcUa", "settings.json");

    /// <summary>
    /// The saved settings, or the defaults with <paramref name="problem"/> saying
    /// why. An unreadable file is kept as settings.json.unreadable, since the
    /// next save overwrites it.
    /// </summary>
    public static PluginSettings Load(out string? problem)
    {
        problem = null;
        try
        {
            if (File.Exists(FilePath))
                return (JsonSerializer.Deserialize<PluginSettings>(File.ReadAllText(FilePath)) ?? new PluginSettings()).Normalized();
        }
        catch (Exception ex)
        {
            problem = $"Settings could not be read, using defaults: {ex.Message}";
            try
            {
                File.Copy(FilePath, FilePath + ".unreadable", overwrite: true);
                problem += $" The file was kept as {FilePath}.unreadable.";
            }
            catch (Exception) { /* the defaults work without it */ }
        }
        return new PluginSettings();
    }

    /// <summary>Lists a hand-edited file set to null are empty lists again.</summary>
    private PluginSettings Normalized()
    {
        NodeSetFolders ??= new();
        RecentEndpoints ??= new();
        CompletedLessons ??= new();
        if (MirrorMaxNodes <= 0) MirrorMaxNodes = OpcUaAml.Server.MirrorOptions.DefaultMaxNodes;
        return this;
    }

    public void Save()
    {
        try
        {
            // Written beside and moved over, so a crash or a second editor never leaves half a file.
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            var temp = FilePath + "." + Environment.ProcessId + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temp, FilePath, overwrite: true);
        }
        catch (Exception ex)
        {
            PluginLog.Warn($"Settings could not be saved: {ex.Message}");
        }
    }
}
