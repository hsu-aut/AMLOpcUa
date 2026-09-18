// uaaml: the command line face of OpcUaAml.Core. Everything the plugin does
// can be done here too, so it can be scripted and tested without the editor.

using Aml.Engine.CAEX;
using OpcUaAml.Compare;
using OpcUaAml.Import;
using OpcUaAml.NodeSets;

namespace OpcUaAml.Tool;

public static class Program
{
    private const string Usage = """
        uaaml: OPC UA and AutomationML

        uaaml info <NodeSet.xml> [--search <dir>]...
            Model, version and required models of a NodeSet, and which of them
            cannot be found.

        uaaml import <NodeSet.xml> [--search <dir>]... [--into <doc.aml>] [-o <out.aml>] [--keep]
            Convert the NodeSet according to OPC 10000-83 Annex A and write its
            AML libraries. With --into, the libraries are merged into that CAEX 3.0
            document, which is overwritten unless -o names another file. Without
            --into, -o is required and receives a new document.
            --keep   keep libraries the document already has instead of replacing them

        uaaml compare <left.aml|amlx> <right.aml|amlx> [--skeleton] [--limit <n>]
            Structural difference of the class libraries of two documents.
            --skeleton   libraries, classes, derivation and child elements only

        The UA base model and DI ship with uaaml; --search adds folders with
        further NodeSets, which win over the bundled ones when newer.
        """;

    public static int Main(string[] args)
    {
        try
        {
            if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
            {
                Console.WriteLine(Usage);
                return args.Length == 0 ? 2 : 0;
            }

            var rest = args.Skip(1).ToList();
            return args[0] switch
            {
                "info" => Info(rest),
                "import" => Import(rest),
                "compare" => CompareCommand(rest),
                _ => Fail($"Unknown command '{args[0]}'.\n\n{Usage}"),
            };
        }
        catch (ImportException ex)
        {
            return Fail(ex.Message);
        }
        catch (ArgumentException ex)
        {
            return Fail(ex.Message + "\n\n" + Usage);
        }
    }

    private static int Info(List<string> args)
    {
        var options = Options.Parse(args, valued: new[] { "--search" }, flags: Array.Empty<string>());
        var file = options.SinglePositional("NodeSet file");
        var catalog = NodeSetCatalog.Create(options.All("--search"));
        var info = NodeSetInfo.TryRead(file) ?? throw new ImportException($"'{file}' is not an OPC UA NodeSet.");

        foreach (var model in info.Models)
        {
            Console.WriteLine($"Model     {model.Model.ModelUri}");
            Console.WriteLine($"Version   {model.Model.Version ?? "-"}");
            Console.WriteLine($"Published {model.Model.PublicationDate?.ToString("yyyy-MM-dd") ?? "-"}");
            foreach (var req in model.RequiredModels)
            {
                var provider = catalog.Find(req.ModelUri);
                var where = provider == null ? "MISSING" : provider.FilePath;
                Console.WriteLine($"Requires  {req.ModelUri} {req.Version} -> {where}");
            }
        }
        var missing = catalog.MissingDependencies(info);
        foreach (var w in catalog.Warnings) Console.WriteLine($"Note      {w}");
        if (missing.Count == 0) return 0;
        Console.WriteLine($"Missing (transitively): {string.Join(", ", missing.Select(m => m.ModelUri))}");
        return 1;
    }

    private static int Import(List<string> args)
    {
        var options = Options.Parse(args, valued: new[] { "--search", "--into", "-o" }, flags: new[] { "--keep" });
        var file = options.SinglePositional("NodeSet file");
        var into = options.One("--into");
        var output = options.One("-o") ?? into
            ?? throw new ArgumentException("Give -o <out.aml>, or --into <doc.aml> to merge into a document.");

        var catalog = NodeSetCatalog.Create(options.All("--search"));
        foreach (var w in catalog.Warnings) Console.Error.WriteLine($"note: {w}");

        var target = into != null ? Documents.Load(into) : CAEXDocument.New_CAEXDocument();
        var result = OpcUaImport.ImportInto(target, file, catalog,
            new MergeOptions { ReplaceGeneratedLibraries = !options.Has("--keep") });

        foreach (var w in result.Warnings) Console.Error.WriteLine($"warning: {w}");
        foreach (var c in result.Merge.Changes) Console.WriteLine($"{c.Action,-8} {c.Kind,-18} {c.Name}");
        Documents.Save(target, output);
        Console.WriteLine(result.Summary);
        Console.WriteLine($"Written to {Path.GetFullPath(output)}");
        return 0;
    }

    private static int CompareCommand(List<string> args)
    {
        var options = Options.Parse(args, valued: new[] { "--limit" }, flags: new[] { "--skeleton" });
        if (options.Positional.Count != 2) throw new ArgumentException("compare needs two documents.");
        var limit = int.TryParse(options.One("--limit"), out var n) ? n : 200;

        var comparer = new LibraryComparer { CompareFacets = !options.Has("--skeleton") };
        var diffs = comparer.Compare(Documents.Load(options.Positional[0]), Documents.Load(options.Positional[1]));

        foreach (var d in diffs.Take(limit)) Console.WriteLine(d);
        if (diffs.Count > limit) Console.WriteLine($"... {diffs.Count - limit} more");
        Console.WriteLine();
        foreach (var (key, count, _) in LibraryComparer.Summarize(diffs).Take(30))
            Console.WriteLine($"{count,7}  {key}");
        Console.WriteLine($"{diffs.Count} difference(s).");
        return diffs.Count == 0 ? 0 : 1;
    }

    private static int Fail(string message)
    {
        Console.Error.WriteLine("error: " + message);
        return 1;
    }
}

/// <summary>Loading and saving .aml and .amlx the same way.</summary>
public static class Documents
{
    public static CAEXDocument Load(string path)
    {
        if (!File.Exists(path)) throw new ImportException($"'{path}' does not exist.");
        return Path.GetExtension(path).Equals(".amlx", StringComparison.OrdinalIgnoreCase)
            ? NodeSetImporter.ReadContainer(path)
            : CAEXDocument.LoadFromFile(path);
    }

    public static void Save(CAEXDocument doc, string path)
    {
        if (Path.GetExtension(path).Equals(".amlx", StringComparison.OrdinalIgnoreCase))
            throw new ImportException("Writing .amlx is not supported; use .aml.");
        doc.SaveToFile(path, true);
    }
}

/// <summary>A small argument parser: positionals, valued options (repeatable) and flags.</summary>
public sealed class Options
{
    public List<string> Positional { get; } = new();
    private readonly Dictionary<string, List<string>> _values = new();
    private readonly HashSet<string> _flags = new();

    public static Options Parse(IReadOnlyList<string> args, string[] valued, string[] flags)
    {
        var o = new Options();
        for (var i = 0; i < args.Count; i++)
        {
            var a = args[i];
            if (valued.Contains(a))
            {
                if (i + 1 >= args.Count) throw new ArgumentException($"{a} needs a value.");
                if (!o._values.TryGetValue(a, out var list)) o._values[a] = list = new List<string>();
                list.Add(args[++i]);
            }
            else if (flags.Contains(a)) o._flags.Add(a);
            else if (a.StartsWith('-') && a.Length > 1) throw new ArgumentException($"Unknown option '{a}'.");
            else o.Positional.Add(a);
        }
        return o;
    }

    public IReadOnlyList<string> All(string name) => _values.TryGetValue(name, out var v) ? v : Array.Empty<string>();

    public string? One(string name)
    {
        var all = All(name);
        if (all.Count > 1) throw new ArgumentException($"{name} may be given only once.");
        return all.Count == 1 ? all[0] : null;
    }

    public bool Has(string flag) => _flags.Contains(flag);

    public string SinglePositional(string what) =>
        Positional.Count == 1 ? Positional[0] : throw new ArgumentException($"Expected exactly one {what}.");
}
