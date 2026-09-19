// The OPC Foundation's ModelCompiler as an external tool: it turns a
// ModelDesign file into a NodeSet, which is the form everything here works
// with. The compiler is not shipped and not required; it is looked for where
// its installer puts it, and when it is missing the message says how to get it.
//
// Nothing of the compiler's code generation is used: only "compile", and only
// its NodeSet.

using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Xml.Linq;

namespace OpcUaAml.ModelDesign;

public sealed class ModelCompilerException(string message) : Exception(message);

public sealed class CompileOptions
{
    /// <summary>The compiler to run; looked for when absent.</summary>
    public string? Executable { get; init; }

    /// <summary>Further design or NodeSet files the model needs.</summary>
    public IReadOnlyList<string> Included { get; init; } = [];

    /// <summary>The identifier file (CSV); created beside the design when absent.</summary>
    public string? IdentifierFile { get; init; }

    /// <summary>The version of the specification the model follows (v103, v104, v105).</summary>
    public string? SpecificationVersion { get; init; }

    /// <summary>
    /// The first identifier the compiler hands out to the nodes it generates
    /// itself (arguments, encodings, the names of an enumeration). Left out, it
    /// starts above the highest identifier the design already names, so the
    /// nodes that carry one keep it.
    /// </summary>
    public uint? StartId { get; init; }

    public TimeSpan Timeout { get; init; } = TimeSpan.FromMinutes(5);
}

/// <summary>What one run produced: the NodeSet, every file written, and what the compiler said.</summary>
public sealed record CompileResult(string NodeSetPath, IReadOnlyList<string> Files, string Output);

public static class ModelCompilerTool
{
    public const string PackageId = "OPCFoundation.Opc.Ua.ModelCompiler.Tool";
    public const string CommandName = "Opc.Ua.ModelCompiler";

    /// <summary>An environment variable that names the compiler, for an installation of one's own.</summary>
    public const string PathVariable = "AMLOPCUA_MODELCOMPILER";

    public static string InstallHint =>
        $"The OPC UA ModelCompiler was not found. Install it with\n"
        + $"    dotnet tool install --global {PackageId}\n"
        + $"or name it in the environment variable {PathVariable}.";

    /// <summary>The compiler's path, or null when it is not installed.</summary>
    public static string? Locate(string? preferred = null)
    {
        foreach (var candidate in Candidates(preferred))
        {
            if (candidate is { Length: > 0 } && File.Exists(candidate)) return candidate;
        }
        return null;
    }

    private static IEnumerable<string?> Candidates(string? preferred)
    {
        var windows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
        var executable = windows ? CommandName + ".exe" : CommandName;

        yield return preferred;
        yield return Environment.GetEnvironmentVariable(PathVariable);
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (home.Length > 0) yield return Path.Combine(home, ".dotnet", "tools", executable);
        foreach (var folder in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            if (folder.Length > 0) yield return Path.Combine(folder.Trim('"'), executable);
        }
    }

    /// <summary>
    /// Compiles a ModelDesign (or a NodeSet, which the compiler also reads)
    /// and returns the NodeSet it wrote into <paramref name="outputFolder"/>.
    /// </summary>
    /// <exception cref="ModelCompilerException">The compiler is missing, failed, or wrote no NodeSet.</exception>
    public static async Task<CompileResult> CompileAsync(
        string designPath, string outputFolder, CompileOptions? options = null, CancellationToken cancellationToken = default)
    {
        options ??= new CompileOptions();
        if (!File.Exists(designPath)) throw new ModelCompilerException($"'{designPath}' does not exist.");
        var compiler = Locate(options.Executable) ?? throw new ModelCompilerException(InstallHint);
        Directory.CreateDirectory(outputFolder);

        // The identifiers live in a CSV beside the design, as the OPC
        // Foundation's models keep them; where there is none, the compiler
        // writes one and hands out identifiers itself.
        var beside = Path.ChangeExtension(Path.GetFullPath(designPath), ".csv");
        var identifiers = options.IdentifierFile ?? (File.Exists(beside) ? beside : null);
        var generate = identifiers is null;
        identifiers ??= Path.Combine(outputFolder, Path.GetFileNameWithoutExtension(designPath) + ".csv");
        var before = Files(outputFolder);

        var start = new ProcessStartInfo(compiler)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(Path.GetFullPath(designPath)) ?? Environment.CurrentDirectory,
        };
        start.ArgumentList.Add("compile");
        start.ArgumentList.Add("-d2");
        start.ArgumentList.Add(Path.GetFullPath(designPath));
        foreach (var included in options.Included)
        {
            start.ArgumentList.Add("-d2");
            start.ArgumentList.Add(Path.GetFullPath(included));
        }
        start.ArgumentList.Add(generate ? "-cg" : "-c");
        start.ArgumentList.Add(Path.GetFullPath(identifiers));
        start.ArgumentList.Add("-o2");
        start.ArgumentList.Add(Path.GetFullPath(outputFolder));
        if (options.SpecificationVersion is { Length: > 0 } version)
        {
            start.ArgumentList.Add("-version");
            start.ArgumentList.Add(version);
        }
        if ((options.StartId ?? NextFreeId(designPath)) is { } startId)
        {
            start.ArgumentList.Add("-id");
            start.ArgumentList.Add(startId.ToString(CultureInfo.InvariantCulture));
        }

        var (exitCode, output) = await RunAsync(start, options.Timeout, cancellationToken).ConfigureAwait(false);
        if (exitCode != 0)
            throw new ModelCompilerException($"The ModelCompiler failed (exit code {exitCode}).\n{Tail(output)}");

        var written = Files(outputFolder).Except(before, StringComparer.OrdinalIgnoreCase).ToList();
        var nodeSet = written.Concat(Files(outputFolder))
            .FirstOrDefault(f => f.EndsWith(".NodeSet2.xml", StringComparison.OrdinalIgnoreCase));
        if (nodeSet is null)
            throw new ModelCompilerException($"The ModelCompiler wrote no NodeSet into '{outputFolder}'.\n{Tail(output)}");
        return new CompileResult(nodeSet, written, output);
    }

    private static async Task<(int ExitCode, string Output)> RunAsync(
        ProcessStartInfo start, TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var process = new Process { StartInfo = start };
        var output = new System.Text.StringBuilder();
        process.OutputDataReceived += (_, e) => { if (e.Data != null) lock (output) output.AppendLine(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (output) output.AppendLine(e.Data); };

        try
        {
            process.Start();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            throw new ModelCompilerException($"The ModelCompiler could not be started: {ex.Message}");
        }
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limit.CancelAfter(timeout);
        try
        {
            await process.WaitForExitAsync(limit.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            Kill(process);
            throw new ModelCompilerException($"The ModelCompiler did not finish within {timeout.TotalMinutes:0} minutes.");
        }
        catch (OperationCanceledException)
        {
            Kill(process);
            throw;
        }
        lock (output) return (process.ExitCode, output.ToString());
    }

    private static void Kill(Process process)
    {
        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException or System.ComponentModel.Win32Exception)
        {
            // The process ended on its own; nothing to kill.
        }
    }

    /// <summary>
    /// One above the highest identifier a design names, so the compiler numbers
    /// the nodes it adds itself without taking an identifier that is in use.
    /// Null when the file names none, or is no design.
    /// </summary>
    private static uint? NextFreeId(string designPath)
    {
        try
        {
            var design = SafeXml.Load(designPath, LoadOptions.None);
            if (design.Root?.Name.NamespaceName != ModelDesignWriter.DesignNamespace) return null;
            var highest = design.Descendants()
                .Select(e => (string?)e.Attribute("NumericId"))
                .Where(id => id is { Length: > 0 })
                .Select(id => uint.TryParse(id, CultureInfo.InvariantCulture, out var value) ? value : 0u)
                .DefaultIfEmpty(0u)
                .Max();
            return highest == 0 ? null : highest + 1;
        }
        catch (Exception ex) when (ex is IOException or System.Xml.XmlException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static List<string> Files(string folder) =>
        Directory.Exists(folder) ? Directory.GetFiles(folder, "*", SearchOption.AllDirectories).ToList() : [];

    /// <summary>The last lines of the compiler's output, which carry its message.</summary>
    private static string Tail(string output, int lines = 12)
    {
        var all = output.Split('\n').Select(l => l.TrimEnd('\r')).Where(l => l.Trim().Length > 0).ToList();
        return string.Join('\n', all.Skip(Math.Max(0, all.Count - lines)));
    }
}
