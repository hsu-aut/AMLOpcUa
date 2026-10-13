using System.IO;
using Aml.Editor.Plugin.OpcUa;
using OpcUaAml.Import;

namespace PluginTests;

/// <summary>
/// Inside the AutomationML Editor the plugin converts in a process of its own,
/// with the converter it brings in its "converter" folder. These tests run the
/// converter of the plugin's build.
/// </summary>
public class ConverterProcessTests
{
    private static string Repository()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "AMLOpcUa.sln"))) return dir.FullName;
        throw new DirectoryNotFoundException("AMLOpcUa.sln was not found above the test folder.");
    }

    /// <summary>The converter in the plugin's build output, of the configuration these tests run in.</summary>
    private static string Converter()
    {
        var configuration = AppContext.BaseDirectory.Contains($"{Path.DirectorySeparatorChar}Release{Path.DirectorySeparatorChar}") ? "Release" : "Debug";
        return Path.Combine(Repository(), "build", "Plugins", "Aml.Editor.Plugin.OpcUa", configuration, "net8.0-windows7.0", "converter", "uaaml.exe");
    }

    private static string Mini() =>
        Path.Combine(Repository(), "dotnet", "OpcUaAml.Tests", "Fixtures", "modeldesign", "Mini.NodeSet2.xml");

    [Fact]
    public async Task The_converter_the_plugin_brings_converts_a_NodeSet()
    {
        var (result, _) = await ConverterProcess.ConvertAsync(Mini(), Array.Empty<string>(), Converter());

        Assert.Contains(result.Document.CAEXFile.SystemUnitClassLib, l => l.Name == "SUC_http://example.org/Mini/");
        Assert.Equal("http://example.org/Mini/", result.NodeSet.PrimaryModel.Model.ModelUri);
    }

    [Fact]
    public async Task What_the_converter_reports_reaches_the_plugin_as_an_import_error()
    {
        var notNodeSet = Path.Combine(Repository(), "AMLOpcUa.sln");

        var error = await Assert.ThrowsAsync<ImportException>(() => ConverterProcess.ConvertAsync(notNodeSet, Array.Empty<string>(), Converter()));

        Assert.Contains("is not an OPC UA NodeSet", error.Message);
    }

    [Fact]
    public async Task A_missing_converter_is_named()
    {
        var missing = Path.Combine(Path.GetTempPath(), "no-converter-here", "uaaml.exe");

        var error = await Assert.ThrowsAsync<ImportException>(() => ConverterProcess.ConvertAsync(Mini(), Array.Empty<string>(), missing));

        Assert.Contains(missing, error.Message);
    }
}
