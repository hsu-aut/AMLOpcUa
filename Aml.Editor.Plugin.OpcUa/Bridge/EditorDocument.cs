// Where the open container is, read from the AutomationML Editor's own view
// model: the editor gives a plugin no path for a container (ChangeAMLFilePath
// is for files of their own, it publishes the container before it has set it
// up, and its API names an empty file). Its main view model knows it: whether
// a container is open (IsContainer) and its file (AMLContainerEditor.
// ContainerFilename), internal but reachable through the main window's
// DataContext, as EditorSaver reaches the save command.
//
// Brittle in the same way: a later editor may rename them. Every read is
// wrapped, and the worst case is "not known", which the plugin handled before.

using System.IO;
using System.Reflection;
using System.Windows;
using Aml.Editor.Plugin.OpcUa.Diagnostics;

namespace Aml.Editor.Plugin.OpcUa.Bridge;

public static class EditorDocument
{
    private const BindingFlags Any = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

    /// <summary>The .amlx file of the container the editor has open; null for a document of its own or when it cannot be read.</summary>
    public static string? ContainerPath()
    {
        try
        {
            var vm = Application.Current?.MainWindow?.DataContext;
            if (vm == null) return null;
            var type = vm.GetType();
            if (type.GetProperty("IsContainer", Any)?.GetValue(vm) is not true) return null;
            var editor = type.GetProperty("AMLContainerEditor", Any)?.GetValue(vm);
            var path = editor?.GetType().GetProperty("ContainerFilename", Any)?.GetValue(editor) as string;
            PluginLog.Debug($"The editor's container: '{path}'.");
            return !string.IsNullOrEmpty(path) && Path.IsPathRooted(path) && File.Exists(path) ? path : null;
        }
        catch (Exception ex)
        {
            PluginLog.Debug($"The editor's container is not known: {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }
}
