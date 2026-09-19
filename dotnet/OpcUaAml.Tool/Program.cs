// uaaml: the command line face of OpcUaAml.Core. Everything the plugin does
// can be done here too, so it can be scripted and tested without the editor.

using Aml.Engine.CAEX;
using Aml.Engine.CAEX.Extensions;
using OpcUaAml.Checks;
using OpcUaAml.Compare;
using OpcUaAml.Export;
using OpcUaAml.Import;
using OpcUaAml.NodeSets;
using OpcUaAml.Types;

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

        uaaml types <doc.aml> [--filter <text>] [--abstract]
            The UA types (SystemUnitClasses of SUC_ libraries) in a document.

        uaaml instantiate <doc.aml> --type <name|path> --name <name> [--hierarchy <name>]
                          [--optional <a,b/c|all>] [--allow-abstract] [-o <out.aml>]
            Create an instance with every Mandatory child and the chosen Optional
            children (paths relative to the instance) in the instance hierarchy
            (created if missing, default "OpcUaInstances").

        uaaml check <doc.aml>
            Check the instance hierarchies against their UA types. Exits with 1
            when there are errors.

        uaaml export <doc.aml|amlx> -o <out.xml> [--date <yyyy-mm-dd>] [--xslt-compatible]
            Convert the document (CAEX 2.15 or 3.0) into a UANodeSet by the rules
            of the AutomationML/OPC Foundation working group (AML-UA-XSLT).
            --date             PublicationDate of the generated models (default: now)
            --xslt-compatible  reproduce the XSLT output exactly, bugs included

        uaaml browse <endpoint> [<nsu=...;s=...>] [--secure] [--accept]
            Children of a node of a running server (Objects when omitted).

        uaaml mirror <endpoint> <node> <doc.aml> [--hierarchy <name>] [--plan <hierarchy>] [--no-link] [--depth <n>] [--secure] [--accept] [-o <out.aml>]
            Take the node and the nodes below it into the document, with NodeIds,
            UA types (where the document holds them) and current values. A node the
            document already models (same NodeId, in --plan or anywhere) is linked
            to that planned element with refBaseObj, unless --no-link.

        uaaml nodeset <endpoint> [<namespace-uri>] [-o <out.xml>] [--into <doc.aml>] [--instances] [--browse] [--search <dir>]... [--secure] [--accept]
            The server's namespaces, or the NodeSet of one of them: the file the
            server publishes for it, else rebuilt by browsing its types (with
            --instances also its objects; --browse ignores a published file).
            -o writes it. --into imports it into a document (Annex A, saved to -o
            or in place), with the models it requires that neither --search nor
            the bundled NodeSets provide, fetched from the server too.

        uaaml snapshot [<endpoint>] <doc.aml> [--secure] [--accept] [-o <out.aml>]
            Read the current value of every bound element and DataVariable. Without
            an endpoint, from every server the document names as a data source.

        uaaml serve <doc.aml> [--port <n>]
            Serve the document's instance hierarchies as an OPC UA server until Enter.

        uaaml diagram <doc.aml> (--type <name|path> | --instance <name|id>) [--depth <n>] -o <out.svg>
            Draw a UA type or an instance as SVG.

        uaaml upgrade <doc.aml> [-o <out.aml>]
            Add the Mandatory children that updated types now declare.

        uaaml link <doc.aml> --from <element> --to <element> [-o <out.aml>]
            Link a VDI 3682 TechnicalResource to a UA object or a ProcessOperator
            to a UA method (elements by name or ID).

        uaaml cloud search <keywords...> (--user <u> --password <p> | --api-key <k>)
        uaaml cloud download <id> <folder> (--user <u> --password <p> | --api-key <k>)
            Search the UA Cloud Library, or download a model with the models it requires.

        uaaml roundtrip <file>... [--search <dir>]... [-o <report.md>]
            Run each file through both mappings and back and report what survives:
            a NodeSet (.xml) UA -> AML (Annex A) -> UA (AML-UA-XSLT rules),
            an AML document (.aml) AML -> UA (AML-UA-XSLT rules) -> AML (Annex A).

        uaaml compare <left.aml|amlx> <right.aml|amlx> [--skeleton] [--limit <n>]
            Structural difference of the class libraries of two documents.
            --skeleton   libraries, classes, derivation and child elements only
            Given two NodeSets, compares them as graphs: namespaces, aliases,
            models, nodes and references.

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
                "export" => ExportCommand(rest),
                "compare" => CompareCommand(rest),
                "types" => TypesCommand(rest),
                "instantiate" => InstantiateCommand(rest),
                "check" => CheckCommand(rest),
                "roundtrip" => RoundtripCommand(rest),
                "browse" => Run(BrowseCommand(rest)),
                "mirror" => Run(MirrorCommand(rest)),
                "snapshot" => Run(SnapshotCommand(rest)),
                "nodeset" => Run(NodeSetCommand(rest)),
                "serve" => Run(ServeCommand(rest)),
                "diagram" => DiagramCommand(rest),
                "upgrade" => UpgradeCommand(rest),
                "link" => LinkCommand(rest),
                "cloud" => Run(CloudCommand(rest)),
                _ => Fail($"Unknown command '{args[0]}'.\n\n{Usage}"),
            };
        }
        catch (ImportException ex)
        {
            return Fail(ex.Message);
        }
        catch (InstantiationException ex)
        {
            return Fail(ex.Message);
        }
        catch (Exception ex) when (ex is OpcUaAml.Server.UaConnectionException or OpcUaAml.Links.LinkException
                                   or OpcUaAml.NodeSets.CloudLibraryException or OpcUaAml.Addressing.AddressingException)
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

    private static int ExportCommand(List<string> args)
    {
        var options = Options.Parse(args, valued: new[] { "-o", "--date" }, flags: new[] { "--xslt-compatible" });
        var file = options.SinglePositional("document");
        var output = options.One("-o") ?? throw new ArgumentException("Give -o <out.xml>.");
        if (!File.Exists(file)) throw new ImportException($"'{file}' does not exist.");
        DateTime? date = null;
        if (options.One("--date") is { } d)
        {
            if (!DateTime.TryParse(d, System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out var parsed))
                throw new ArgumentException($"--date: '{d}' is not a date.");
            date = parsed;
        }
        NodeSetExporter.ExportFile(file, output, new NodeSetExportOptions
        {
            PublicationDate = date,
            XsltCompatibility = options.Has("--xslt-compatible"),
        });
        Console.WriteLine($"Written to {Path.GetFullPath(output)}");
        return 0;
    }

    private static int CompareCommand(List<string> args)
    {
        var options = Options.Parse(args, valued: new[] { "--limit" }, flags: new[] { "--skeleton" });
        if (options.Positional.Count != 2) throw new ArgumentException("compare needs two documents.");
        var limit = int.TryParse(options.One("--limit"), out var n) ? n : 200;
        if (IsNodeSet(options.Positional[0]) && IsNodeSet(options.Positional[1]))
        {
            var nodeSetDiffs = new NodeSetComparer().Compare(options.Positional[0], options.Positional[1]);
            foreach (var d in nodeSetDiffs.Take(limit)) Console.WriteLine(d);
            if (nodeSetDiffs.Count > limit) Console.WriteLine($"... {nodeSetDiffs.Count - limit} more");
            Console.WriteLine($"{nodeSetDiffs.Count} difference(s).");
            return nodeSetDiffs.Count == 0 ? 0 : 1;
        }

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

    private static bool IsNodeSet(string path) =>
        File.Exists(path) && Path.GetExtension(path).Equals(".xml", StringComparison.OrdinalIgnoreCase)
        && NodeSetInfo.TryRead(path) != null;

    private static int TypesCommand(List<string> args)
    {
        var options = Options.Parse(args, valued: new[] { "--filter" }, flags: new[] { "--abstract" });
        var doc = Documents.Load(options.SinglePositional("document"));
        var filter = options.One("--filter");
        var count = 0;
        foreach (var (path, type) in UaTypes.AllTypes(doc))
        {
            var isAbstract = UaTypes.IsAbstract(type);
            if (isAbstract && !options.Has("--abstract")) continue;
            if (filter != null && !path.Contains(filter, StringComparison.OrdinalIgnoreCase)) continue;
            Console.WriteLine((isAbstract ? "(abstract) " : "") + path);
            count++;
        }
        Console.WriteLine($"{count} type(s).");
        return 0;
    }

    private static int InstantiateCommand(List<string> args)
    {
        var options = Options.Parse(args,
            valued: new[] { "--type", "--name", "--hierarchy", "--optional", "-o" },
            flags: new[] { "--allow-abstract" });
        var file = options.SinglePositional("document");
        var doc = Documents.Load(file);
        var typeArg = options.One("--type") ?? throw new ArgumentException("--type is required.");
        var name = options.One("--name") ?? throw new ArgumentException("--name is required.");

        var type = ResolveType(doc, typeArg);
        var optional = options.One("--optional");
        var chosen = optional == null ? new HashSet<string>()
            : optional.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToHashSet();
        var result = TypeInstantiator.Instantiate(type, name, new InstantiationOptions
        {
            IncludeOptional = p => optional == "all" || chosen.Contains(p),
            AllowAbstract = options.Has("--allow-abstract"),
        });

        var hierarchyName = options.One("--hierarchy") ?? "OpcUaInstances";
        var ih = doc.CAEXFile.InstanceHierarchy[hierarchyName] ?? doc.CAEXFile.InstanceHierarchy.Append(hierarchyName);
        ih.InternalElement.Insert(result.Instance, asFirst: false);

        Console.WriteLine($"Created '{name}' of {type.Name} in '{hierarchyName}': {result.Included.Count} children.");
        if (result.OmittedOptional.Count > 0)
            Console.WriteLine($"Optional, not created: {string.Join(", ", result.OmittedOptional)}");
        if (result.OmittedPlaceholders.Count > 0)
            Console.WriteLine($"Placeholders, add concrete children as needed: {string.Join(", ", result.OmittedPlaceholders)}");
        var output = options.One("-o") ?? file;
        Documents.Save(doc, output);
        Console.WriteLine($"Written to {Path.GetFullPath(output)}");
        return 0;
    }

    private static SystemUnitFamilyType ResolveType(Aml.Engine.CAEX.CAEXDocument doc, string typeArg)
    {
        if (UaTypes.Resolve(doc, typeArg) is { } byPath) return byPath;
        var matches = UaTypes.AllTypes(doc).Where(t => t.Type.Name == typeArg).ToList();
        return matches.Count switch
        {
            1 => matches[0].Type,
            0 => throw new ArgumentException($"No UA type named '{typeArg}' in the document."),
            _ => throw new ArgumentException($"'{typeArg}' is ambiguous; give the path: {string.Join(", ", matches.Select(m => m.Path))}"),
        };
    }

    private static int CheckCommand(List<string> args)
    {
        var options = Options.Parse(args, valued: Array.Empty<string>(), flags: Array.Empty<string>());
        var doc = Documents.Load(options.SinglePositional("document"));
        var findings = AnnexAChecker.Check(doc);
        foreach (var f in findings) Console.WriteLine(f);
        var errors = findings.Count(f => f.Severity == Severity.Error);
        Console.WriteLine($"{errors} error(s), {findings.Count - errors} warning(s).");
        return errors == 0 ? 0 : 1;
    }

    private static int Run(Task<int> task) => task.GetAwaiter().GetResult();

    private static OpcUaAml.Server.UaConnectOptions Connect(Options o, string endpoint) => new()
    {
        EndpointUrl = endpoint,
        UseSecurity = o.Has("--secure"),
        AcceptUntrustedServerCertificates = o.Has("--accept"),
    };

    private static async Task<int> BrowseCommand(List<string> args)
    {
        var o = Options.Parse(args, valued: Array.Empty<string>(), flags: new[] { "--secure", "--accept" });
        if (o.Positional.Count is < 1 or > 2) throw new ArgumentException("browse needs an endpoint and optionally a node.");
        await using var client = await OpcUaAml.Server.UaClient.ConnectAsync(Connect(o, o.Positional[0]));
        var node = o.Positional.Count == 2 ? OpcUaAml.Addressing.UaNodeAddress.Parse(o.Positional[1], client.NamespaceTable) : null;
        foreach (var item in await client.BrowseAsync(node))
            Console.WriteLine($"{item.NodeClass,-9} {item.BrowseName,-30} {item.Address}");
        return 0;
    }

    private static async Task<int> MirrorCommand(List<string> args)
    {
        var o = Options.Parse(args, valued: new[] { "--hierarchy", "--plan", "--depth", "-o" }, flags: new[] { "--secure", "--accept", "--no-link" });
        if (o.Positional.Count != 3) throw new ArgumentException("mirror needs an endpoint, a node and a document.");
        var doc = Documents.Load(o.Positional[2]);
        await using var client = await OpcUaAml.Server.UaClient.ConnectAsync(Connect(o, o.Positional[0]));
        var address = OpcUaAml.Addressing.UaNodeAddress.Parse(o.Positional[1], client.NamespaceTable);
        var start = new OpcUaAml.Server.UaBrowseItem(address, address.Identifier, address.Identifier, "Object", null, "Organizes");
        var ihName = o.One("--hierarchy") ?? "OpcUaServer";
        var ih = doc.CAEXFile.InstanceHierarchy[ihName] ?? doc.CAEXFile.InstanceHierarchy.Append(ihName);
        var depth = int.TryParse(o.One("--depth"), out var d) ? d : 3;
        var plan = o.One("--plan") is { } planName
            ? doc.CAEXFile.InstanceHierarchy[planName] ?? throw new ArgumentException($"No InstanceHierarchy '{planName}'.")
            : null;
        var result = await OpcUaAml.Server.AddressSpaceMirror.MirrorAsync(client, start, ih,
            new OpcUaAml.Server.MirrorOptions { Depth = depth, PlannedIn = plan, LinkToPlanned = !o.Has("--no-link") });
        foreach (var note in result.Notes) Console.Error.WriteLine("note: " + note);
        Documents.Save(doc, o.One("-o") ?? o.Positional[2]);
        Console.WriteLine($"{result.Nodes} node(s), {result.Typed} typed, {result.Linked} linked to the plan{(result.Truncated ? ", truncated" : "")}.");
        return 0;
    }

    private static async Task<int> NodeSetCommand(List<string> args)
    {
        var o = Options.Parse(args, valued: new[] { "-o", "--into", "--search" }, flags: new[] { "--secure", "--accept", "--instances", "--browse" });
        if (o.Positional.Count is < 1 or > 2) throw new ArgumentException("nodeset needs an endpoint and optionally a namespace URI.");
        await using var client = await OpcUaAml.Server.UaClient.ConnectAsync(Connect(o, o.Positional[0]));
        if (o.Positional.Count == 1)
        {
            foreach (var ns in await OpcUaAml.Server.ServerNodeSets.ListAsync(client))
                Console.WriteLine($"{ns.Index,3} {ns.Uri,-50} {ns.Version,-10} {ns.PublicationDate:yyyy-MM-dd} {(ns.HasFile ? "file" : "")}");
            return 0;
        }
        var uri = o.Positional[1];
        var options = new OpcUaAml.Server.ServerNodeSetOptions { IncludeInstances = o.Has("--instances"), PreferNamespaceFile = !o.Has("--browse") };
        var into = o.One("--into");
        var output = o.One("-o");
        if (into == null)
        {
            if (output == null) throw new ArgumentException("Give -o <out.xml>, or --into <doc.aml> to import the types.");
            var set = await OpcUaAml.Server.ServerNodeSets.FetchAsync(client, uri, options);
            foreach (var note in set.Notes) Console.Error.WriteLine("note: " + note);
            set.Document.Save(output);
            Console.WriteLine($"{set.NodeCount} node(s) from the {(set.Source == OpcUaAml.Server.ServerNodeSetSource.NamespaceFile ? "published file" : "server's address space")}, written to {Path.GetFullPath(output)}");
            return 0;
        }

        var catalog = NodeSetCatalog.Create(o.All("--search"));
        var folder = Path.Combine(Path.GetTempPath(), "uaaml-nodesets", Guid.NewGuid().ToString("N")[..8]);
        var files = await OpcUaAml.Server.ServerNodeSets.FetchForImportAsync(client, uri, catalog, folder, options);
        foreach (var set in files.NodeSets)
        {
            foreach (var note in set.Notes) Console.Error.WriteLine($"note ({set.ModelUri}): {note}");
            Console.WriteLine($"{set.ModelUri}: {set.NodeCount} node(s) from the {(set.Source == OpcUaAml.Server.ServerNodeSetSource.NamespaceFile ? "published file" : "server's address space")}");
        }
        var doc = Documents.Load(into);
        var result = OpcUaImport.ImportInto(doc, files.Paths[0], catalog);
        foreach (var w in result.Warnings) Console.Error.WriteLine($"warning: {w}");
        Documents.Save(doc, output ?? into);
        Console.WriteLine(result.Summary);
        return 0;
    }

    private static async Task<int> SnapshotCommand(List<string> args)
    {
        var o = Options.Parse(args, valued: new[] { "-o" }, flags: new[] { "--secure", "--accept" });
        if (o.Positional.Count is not (1 or 2)) throw new ArgumentException("snapshot needs a document, optionally after an endpoint.");
        var docPath = o.Positional[^1];
        var doc = Documents.Load(docPath);

        // Without an endpoint: every server the document names as a data
        // source, with the security its description asks for.
        var connections = o.Positional.Count == 2
            ? new List<OpcUaAml.Server.UaConnectOptions> { Connect(o, o.Positional[0]) }
            : OpcUaAml.Addressing.BprDataVariable.SourcesIn(doc).Where(s => s.Url != null)
                .GroupBy(s => s.Url!.TrimEnd('/').ToLowerInvariant()).Select(g => g.First().ToConnectOptions(o.Has("--accept"))).ToList();
        if (connections.Count == 0) throw new ArgumentException("The document names no server with an EndpointURL or DiscoveryURL; give an endpoint.");

        var failed = 0;
        foreach (var connection in connections)
        {
            await using var client = await OpcUaAml.Server.UaClient.ConnectAsync(connection);
            var result = await OpcUaAml.Server.ValueSnapshot.ApplyAsync(doc, client);
            foreach (var p in result.Problems) Console.Error.WriteLine("warning: " + p);
            Console.WriteLine($"{connection.EndpointUrl}: {result}.");
            failed += result.Failed;
        }
        Documents.Save(doc, o.One("-o") ?? docPath);
        return failed == 0 ? 0 : 1;
    }

    private static async Task<int> ServeCommand(List<string> args)
    {
        var o = Options.Parse(args, valued: new[] { "--port" }, flags: Array.Empty<string>());
        var doc = Documents.Load(o.SinglePositional("document"));
        var port = int.TryParse(o.One("--port"), out var p) ? p : 48400;
        await using var host = await OpcUaAml.Server.AmlServerHost.StartAsync(doc, new OpcUaAml.Server.AmlServerOptions { Port = port });
        Console.WriteLine($"Serving {host.Nodes} node(s) at {host.EndpointUrl}. Press Enter to stop.");
        Console.ReadLine();
        return 0;
    }

    private static int DiagramCommand(List<string> args)
    {
        var o = Options.Parse(args, valued: new[] { "--type", "--instance", "--depth", "-o" }, flags: Array.Empty<string>());
        var doc = Documents.Load(o.SinglePositional("document"));
        SystemUnitClassType root = o.One("--instance") is { } inst
            ? FindElement(doc, inst)
            : ResolveType(doc, o.One("--type") ?? throw new ArgumentException("--type or --instance is required."));
        var depth = int.TryParse(o.One("--depth"), out var d) ? d : 3;
        var output = o.One("-o") ?? throw new ArgumentException("-o <out.svg> is required.");
        var diagram = OpcUaAml.Diagram.DiagramLayout.Apply(OpcUaAml.Diagram.DiagramBuilder.Build(root, depth));
        File.WriteAllText(output, OpcUaAml.Diagram.SvgWriter.Write(diagram));
        Console.WriteLine($"{diagram.Nodes.Count} node(s) written to {Path.GetFullPath(output)}");
        return 0;
    }

    private static int UpgradeCommand(List<string> args)
    {
        var o = Options.Parse(args, valued: new[] { "-o" }, flags: Array.Empty<string>());
        var file = o.SinglePositional("document");
        var doc = Documents.Load(file);
        var changes = InstanceUpgrader.UpgradeDocument(doc);
        foreach (var c in changes) Console.WriteLine($"Added {c.Added} to {c.ElementPath}");
        Documents.Save(doc, o.One("-o") ?? file);
        Console.WriteLine($"{changes.Count} child(ren) added.");
        return 0;
    }

    private static int LinkCommand(List<string> args)
    {
        var o = Options.Parse(args, valued: new[] { "--from", "--to", "-o" }, flags: Array.Empty<string>());
        var file = o.SinglePositional("document");
        var doc = Documents.Load(file);
        var from = FindElement(doc, o.One("--from") ?? throw new ArgumentException("--from is required."));
        var to = FindElement(doc, o.One("--to") ?? throw new ArgumentException("--to is required."));
        var kind = OpcUaAml.Links.Vdi3682Links.FpdElements(doc, OpcUaAml.Links.FpdKind.ProcessOperator).Any(e => e.ID == from.ID)
            ? OpcUaAml.Links.FpdKind.ProcessOperator : OpcUaAml.Links.FpdKind.TechnicalResource;
        var attr = OpcUaAml.Links.Vdi3682Links.Link(from, to, kind);
        Documents.Save(doc, o.One("-o") ?? file);
        Console.WriteLine($"{from.Name}.{attr.Name} = {to.Name}");
        return 0;
    }

    private static async Task<int> CloudCommand(List<string> args)
    {
        var o = Options.Parse(args, valued: new[] { "--user", "--password", "--api-key" }, flags: Array.Empty<string>());
        if (o.Positional.Count < 2) throw new ArgumentException("cloud needs 'search <keywords>' or 'download <id> <folder>'.");
        var client = new CloudLibraryClient(new HttpClient { Timeout = TimeSpan.FromSeconds(60) },
            o.One("--user"), o.One("--password"), o.One("--api-key"));
        switch (o.Positional[0])
        {
            case "search":
                foreach (var m in await client.SearchAsync(o.Positional.Skip(1)))
                    Console.WriteLine($"{m.Identifier,8}  {m.NamespaceUri,-60} {m.Version} {m.PublicationDate:yyyy-MM-dd}  {m.Title}");
                return 0;
            case "download" when o.Positional.Count == 3 && int.TryParse(o.Positional[1], out var id):
                var folder = o.Positional[2];
                var catalog = NodeSetCatalog.Create(new[] { folder });
                foreach (var f in await client.DownloadWithDependenciesAsync(id, folder, catalog)) Console.WriteLine(f);
                return 0;
            default:
                throw new ArgumentException("cloud needs 'search <keywords>' or 'download <id> <folder>'.");
        }
    }

    private static InternalElementType FindElement(CAEXDocument doc, string nameOrId)
    {
        var all = doc.CAEXFile.InstanceHierarchy.SelectMany(ih => ih.Descendants<InternalElementType>()).ToList();
        var matches = all.Where(e => e.ID == nameOrId).ToList();
        if (matches.Count == 0) matches = all.Where(e => e.Name == nameOrId).ToList();
        return matches.Count switch
        {
            1 => matches[0],
            0 => throw new ArgumentException($"No element '{nameOrId}' in the instance hierarchies."),
            _ => throw new ArgumentException($"'{nameOrId}' is ambiguous; give the element's ID."),
        };
    }

    private static int RoundtripCommand(List<string> args)
    {
        var o = Options.Parse(args, valued: new[] { "--search", "-o" }, flags: Array.Empty<string>());
        if (o.Positional.Count == 0) throw new ArgumentException("roundtrip needs at least one file.");
        var report = new System.Text.StringBuilder();
        report.AppendLine("# Round trip report").AppendLine();
        var failed = 0;
        foreach (var file in o.Positional)
        {
            var catalog = NodeSetCatalog.Create(o.All("--search"));
            var result = Path.GetExtension(file).Equals(".xml", StringComparison.OrdinalIgnoreCase)
                ? OpcUaAml.Roundtrip.RoundtripRunner.UaAmlUa(file, catalog)
                : OpcUaAml.Roundtrip.RoundtripRunner.AmlUaAml(file, catalog);
            if (!result.Completed) failed++;
            Console.WriteLine($"{result.Subject}: " + (result.Completed
                ? string.Join(", ", result.Criteria.Where(c => c.Total > 0).Select(c => $"{c.Name} {c.Kept}/{c.Total}"))
                : $"stopped at {result.FailedStep}: {result.Error}"));
            report.AppendLine(result.ToMarkdown());
        }
        if (o.One("-o") is { } output)
        {
            File.WriteAllText(output, report.ToString());
            Console.WriteLine($"Report written to {Path.GetFullPath(output)}");
        }
        return failed == 0 ? 0 : 1;
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
