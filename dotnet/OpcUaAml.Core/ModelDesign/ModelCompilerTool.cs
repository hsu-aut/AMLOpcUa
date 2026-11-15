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

    /// <summary>
    /// Where the NodeSets of the models the design requires are found when
    /// <see cref="Included"/> does not give them; the bundled models when absent.
    /// </summary>
    public OpcUaAml.NodeSets.NodeSetCatalog? Catalog { get; init; }

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
        // An empty identifier file names no id; the compiler hands out its own.
        var identifiers = options.IdentifierFile
                          ?? (File.Exists(beside) && new FileInfo(beside).Length > 0 ? beside : null);
        var given = identifiers;
        var generate = identifiers is null;
        identifiers ??= Path.Combine(outputFolder, Path.GetFileNameWithoutExtension(designPath) + ".csv");
        var started = DateTime.UtcNow.AddSeconds(-1);

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
        foreach (var included in Includes(designPath, options))
        {
            start.ArgumentList.Add("-d2");
            start.ArgumentList.Add(included);
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
        if ((options.StartId ?? NextFreeId(designPath, given)) is { } startId)
        {
            start.ArgumentList.Add("-id");
            start.ArgumentList.Add(startId.ToString(CultureInfo.InvariantCulture));
        }

        var (exitCode, output) = await RunAsync(start, options.Timeout, cancellationToken).ConfigureAwait(false);
        if (exitCode != 0)
            throw new ModelCompilerException($"The ModelCompiler failed (exit code {exitCode}).\n{Tail(output)}");

        // What this run wrote, by the time it was written: a folder used before
        // holds older files of the same name, and the newest is ours.
        var written = Files(outputFolder).Where(f => File.GetLastWriteTimeUtc(f) >= started).ToList();
        var nodeSet = written.Where(f => f.EndsWith(".NodeSet2.xml", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .FirstOrDefault();
        if (nodeSet is null)
            throw new ModelCompilerException($"The ModelCompiler wrote no NodeSet into '{outputFolder}'.\n{Tail(output)}");
        return new CompileResult(nodeSet, written, output);
    }

    /// <summary>The UA base model, which the compiler brings itself.</summary>
    private const string UaNamespace = "http://opcfoundation.org/UA/";

    private static readonly Lazy<OpcUaAml.NodeSets.NodeSetCatalog> BundledCatalog =
        new(() => OpcUaAml.NodeSets.NodeSetCatalog.Create(Array.Empty<string>()));

    /// <summary>
    /// The files the compiler reads besides the design: those given, and the
    /// NodeSets of the models the design requires that none of them holds,
    /// from the catalog, each after the models it builds on. Without them the
    /// compiler stopped with a stack trace on the first type of another model.
    /// </summary>
    /// <exception cref="ModelCompilerException">The design requires a model no file at hand holds.</exception>
    public static IReadOnlyList<string> Includes(string designPath, CompileOptions options)
    {
        var given = options.Included.Select(Path.GetFullPath).ToList();
        var covered = new HashSet<string>(StringComparer.Ordinal) { UaNamespace };
        foreach (var file in given) covered.UnionWith(ModelsOf(file));
        var (own, required) = Requirements(designPath);
        covered.UnionWith(own);

        var catalog = options.Catalog ?? BundledCatalog.Value;
        var found = new List<string>();
        var missing = new List<string>();
        foreach (var uri in required.Where(u => !covered.Contains(u)))
        {
            if (catalog.Find(uri) is not { } provider) { missing.Add(uri); continue; }
            missing.AddRange(catalog.MissingDependencies(provider).Select(m => m.ModelUri).Where(u => !covered.Contains(u)));
            // What a model builds on first: the compiler resolves a BaseType as it reads.
            foreach (var model in catalog.Dependencies(provider).Reverse().Append(provider))
            {
                if (model.Models.Any(m => covered.Contains(m.Model.ModelUri))) continue;
                covered.UnionWith(model.Models.Select(m => m.Model.ModelUri));
                found.Add(Path.GetFullPath(model.FilePath));
            }
        }
        if (missing.Count > 0)
            throw new ModelCompilerException(
                $"The design requires {string.Join(", ", missing.Distinct())}, and no NodeSet of it is at hand. "
                + "Give it with --include, or the folder that holds it with --search.");
        return found.Concat(given).ToList();
    }

    /// <summary>The models a design (TargetNamespace and Namespaces) or a NodeSet declares and requires.</summary>
    private static (IReadOnlyList<string> Own, IReadOnlyList<string> Required) Requirements(string path)
    {
        if (OpcUaAml.NodeSets.NodeSetInfo.TryRead(path) is { } nodeSet)
            return (nodeSet.Models.Select(m => m.Model.ModelUri).ToList(),
                    nodeSet.Models.SelectMany(m => m.RequiredModels).Select(r => r.ModelUri).Distinct().ToList());
        try
        {
            var root = SafeXml.Load(path, LoadOptions.None).Root;
            if (root?.Name.NamespaceName != ModelDesignWriter.DesignNamespace) return ([], []);
            var target = (string?)root.Attribute("TargetNamespace");
            var named = root.Elements(XName.Get("Namespaces", ModelDesignWriter.DesignNamespace))
                .Elements(XName.Get("Namespace", ModelDesignWriter.DesignNamespace))
                .Select(n => n.Value.Trim()).Where(u => u.Length > 0 && u != target).Distinct().ToList();
            return (target is null ? [] : [target], named);
        }
        catch (Exception ex) when (ex is IOException or System.Xml.XmlException or UnauthorizedAccessException)
        {
            // Not readable: the compiler says why when it runs.
            return ([], []);
        }
    }

    /// <summary>The models a given file holds: a NodeSet's, or a design's TargetNamespace.</summary>
    private static IEnumerable<string> ModelsOf(string file)
    {
        var (own, _) = Requirements(file);
        return own;
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
            // What it said before it was stopped is the only clue to where it hung.
            string said;
            lock (output) said = Tail(output.ToString(), 6);
            throw new ModelCompilerException($"The ModelCompiler did not finish within {timeout.TotalMinutes:0} minutes."
                                             + (said.Length > 0 ? $"\n{said}" : ""));
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
    /// One above the highest identifier the model already uses, so the compiler
    /// numbers the nodes it adds itself without taking one that is in use. The
    /// identifiers are in the design (NumericId) or, as this repository writes
    /// them, in the identifier file. Null when neither names one.
    /// </summary>
    private static uint? NextFreeId(string designPath, string? identifierFile)
    {
        var highest = 0u;
        try
        {
            var design = SafeXml.Load(designPath, LoadOptions.None);
            if (design.Root?.Name.NamespaceName == ModelDesignWriter.DesignNamespace)
            {
                foreach (var id in design.Descendants().Select(e => (string?)e.Attribute("NumericId")))
                {
                    if (uint.TryParse(id, CultureInfo.InvariantCulture, out var value)) highest = Math.Max(highest, value);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or System.Xml.XmlException or UnauthorizedAccessException)
        {
            // Not readable: the caller finds out when the compiler runs.
        }
        try
        {
            if (identifierFile != null && File.Exists(identifierFile))
            {
                foreach (var line in File.ReadLines(identifierFile))
                {
                    var fields = line.Split(',');
                    if (fields.Length >= 2 && uint.TryParse(fields[1], CultureInfo.InvariantCulture, out var value))
                        highest = Math.Max(highest, value);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // As above.
        }
        return highest == 0 ? null : highest + 1;
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
