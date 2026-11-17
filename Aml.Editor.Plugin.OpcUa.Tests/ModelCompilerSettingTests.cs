using Aml.Editor.Plugin.OpcUa;
using Aml.Editor.Plugin.OpcUa.Localization;

namespace PluginTests;

/// <summary>
/// Settings › ModelCompiler… chooses the compiler that turns a ModelDesign into a
/// NodeSet. The path was a field of the settings file only, with no way to set it
/// from the plugin.
/// </summary>
public class ModelCompilerSettingTests
{
    private static string Text(string? chosen, string? located)
    {
        var german = L.German;
        L.German = false;
        try { return OpcUaPlugin.ModelCompilerText(chosen, located); }
        finally { L.German = german; }
    }

    [Fact]
    public void Says_the_chosen_compiler_is_used()
    {
        Assert.Equal(@"The ModelCompiler is C:\Tools\Opc.Ua.ModelCompiler.exe.",
            Text(@"C:\Tools\Opc.Ua.ModelCompiler.exe", @"C:\Tools\Opc.Ua.ModelCompiler.exe"));
    }

    [Fact]
    public void Says_which_compiler_is_used_when_the_chosen_one_is_gone()
    {
        Assert.Equal(@"C:\Old\Opc.Ua.ModelCompiler.exe is not there; the ModelCompiler found is C:\Users\me\.dotnet\tools\Opc.Ua.ModelCompiler.exe.",
            Text(@"C:\Old\Opc.Ua.ModelCompiler.exe", @"C:\Users\me\.dotnet\tools\Opc.Ua.ModelCompiler.exe"));
    }

    [Fact]
    public void Says_how_to_install_it_when_there_is_none()
    {
        Assert.StartsWith("No ModelCompiler found. ", Text(null, null));
    }
}
