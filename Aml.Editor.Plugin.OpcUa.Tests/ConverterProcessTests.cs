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

    /// <summary>
    /// The AutomationML Editor 6.4.3.8 runs on .NET 10, so a computer set up
    /// for it may have no .NET 8. Seen with 0.2.0: there the converter did not
    /// start ("You must install or update .NET"), and every import failed.
    /// The converter is run here with the newest runtime of this computer
    /// alone, as such a computer would have it.
    /// </summary>
    [Fact]
    public void The_converter_runs_on_a_computer_with_only_a_newer_NET()
    {
        var installed = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(typeof(object).Assembly.Location)!, "..", "..", ".."));
        var newest = new DirectoryInfo(Path.Combine(installed, "shared", "Microsoft.NETCore.App")).GetDirectories()
            .Select(d => (Dir: d, Version: Version.TryParse(d.Name.Split('-')[0], out var v) ? v : null))
            .Where(r => r.Version is { Major: > 8 })
            .OrderByDescending(r => r.Version)
            .FirstOrDefault();
        Assert.True(newest.Dir != null, $"No .NET runtime newer than 8 in {installed}; the editor needs .NET 10, install it to run this test.");
        var fxr = new DirectoryInfo(Path.Combine(installed, "host", "fxr")).GetDirectories()
            .Where(d => Version.TryParse(d.Name.Split('-')[0], out var v) && v.Major == newest.Version!.Major)
            .OrderByDescending(d => d.Name)
            .First();

        var root = Directory.CreateTempSubdirectory("opcuaaml-runtime-");
        var links = new[]
        {
            (Link: Path.Combine(root.FullName, "host", "fxr", fxr.Name), Target: fxr.FullName),
            (Link: Path.Combine(root.FullName, "shared", "Microsoft.NETCore.App", newest.Dir!.Name), Target: newest.Dir.FullName),
        };
        try
        {
            foreach (var (link, target) in links)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(link)!);
                Run("cmd.exe", new[] { "/c", "mklink", "/J", link, target }, null);
            }
            var output = Path.Combine(root.FullName, "converted.aml");

            var (exitCode, errors) = Run(Converter(), new[] { "convert", Mini(), "-o", output }, root.FullName);

            Assert.True(exitCode == 0, $"The converter ended with {exitCode} on .NET {newest.Version} alone: {errors}");
            Assert.True(File.Exists(output));
        }
        finally
        {
            // The links first, on their own, so nothing of the real runtime is touched.
            foreach (var (link, _) in links)
                if (Directory.Exists(link)) Directory.Delete(link);
            root.Delete(recursive: true);
        }
    }

    /// <summary>Runs a program; with <paramref name="dotnetRoot"/>, it sees the runtimes there and no others.</summary>
    private static (int ExitCode, string Errors) Run(string program, IEnumerable<string> arguments, string? dotnetRoot)
    {
        var start = new System.Diagnostics.ProcessStartInfo(program)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        if (dotnetRoot != null)
        {
            start.Environment["DOTNET_ROOT"] = dotnetRoot;
            start.Environment["DOTNET_ROOT_X64"] = dotnetRoot;
            start.Environment.Remove("DOTNET_ROLL_FORWARD");
        }
        using var process = System.Diagnostics.Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var errors = process.StandardError.ReadToEnd();
        process.WaitForExit();
        output.Wait();
        if (dotnetRoot == null && process.ExitCode != 0) throw new InvalidOperationException($"{program} failed: {errors}{output.Result}");
        return (process.ExitCode, errors + output.Result);
    }

    [Fact]
    public async Task A_missing_converter_is_named()
    {
        var missing = Path.Combine(Path.GetTempPath(), "no-converter-here", "uaaml.exe");

        var error = await Assert.ThrowsAsync<ImportException>(() => ConverterProcess.ConvertAsync(Mini(), Array.Empty<string>(), missing));

        Assert.Contains(missing, error.Message);
    }
}
