using System.IO;
using System.Reflection;
using Aml.Editor.Plugin.OpcUa.Diagnostics;

namespace PluginTests;

/// <summary>
/// The log file after a write failed (disk full, the file taken away): the
/// writer was dropped "to be opened again on the next cycle", and nothing
/// opened it again, so the file stayed silent for the rest of the session.
/// </summary>
public class PluginLogFileTests
{
    private static string ReadShared(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        return new StreamReader(stream).ReadToEnd();
    }

    [Fact]
    public void After_a_failed_write_the_log_file_is_written_again()
    {
        PluginLog.Init();
        var path = PluginLog.FilePath;
        Assert.NotEmpty(path);
        var writer = (StreamWriter)typeof(PluginLog).GetField("_writer", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;

        writer.Dispose(); // the next write fails, as on a full disk
        PluginLog.Info("this line is lost");
        var marker = "back again " + Guid.NewGuid().ToString("N");
        PluginLog.Info(marker);

        Assert.Contains(marker, ReadShared(path));
    }
}
