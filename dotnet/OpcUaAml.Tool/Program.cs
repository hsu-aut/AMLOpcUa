// uaaml: the command line face of OpcUaAml.Core. Everything the plugin does
// can be done here too, so it can be scripted and tested without the editor.

using Aml.Engine.CAEX;
using Aml.Engine.CAEX.Extensions;
using OpcUaAml.Checks;
using OpcUaAml.Compare;
using OpcUaAml.Export;
using OpcUaAml.Import;
using OpcUaAml.ModelDesign;
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
            AML libraries. With --into, the libraries are merged into that document
            (a CAEX 2.15 document is converted to CAEX 3.0 first), which is
            overwritten unless -o names another file. Without --into, -o is
            required and receives a new document.
            --keep   keep libraries the document already has instead of replacing them

        uaaml types <doc.aml> [--filter <text>] [--abstract]
            The UA types (SystemUnitClasses of SUC_ libraries) in a document.

        uaaml instantiate <doc.aml> --type <name|path> --name <name> [--hierarchy <name>]
                          [--optional <a,b/c|all>] [--fill <placeholder>=<name>[:<type>],...]...
                          [--allow-abstract] [-o <out.aml>]
            Create an instance with every Mandatory child and the chosen Optional
            children (paths relative to the instance) in the instance hierarchy
            (created if missing, default "OpcUaInstances"). --fill creates concrete
            children for a placeholder, of its type or of the given one, e.g.
            --fill "<ObjectIdentifier>=ModuleA,Firmware:SoftwareVersionType".

        uaaml check <doc.aml>
            Check the instance hierarchies against their UA types. Exits with 1
            when there are errors.

        uaaml export <doc.aml|amlx> -o <out.xml> [--date <yyyy-mm-dd>] [--xslt-compatible]
                     [--annex-a-inverse [--namespace <uri>]]
            Convert the document (CAEX 2.15 or 3.0) into a UANodeSet by the rules
            of the AutomationML/OPC Foundation working group (AML-UA-XSLT).
            --date             PublicationDate of the generated models (default: now)
            --xslt-compatible  reproduce the XSLT output exactly, bugs included
            --annex-a-inverse  instead write back the OPC UA model that OPC 10000-83
                               Annex A put into the document: its types, DataTypes,
                               ReferenceTypes, methods and objects as the nodes they
                               were. Not a standard. --namespace names the model when
                               the document holds several.

        uaaml browse <endpoint> [<nsu=...;s=...>] [--insecure] [--accept]
            Children of a node of a running server (Objects when omitted).
            Connections to servers are secured; a server without a secured
            endpoint is refused unless --insecure. --accept trusts an unknown
            server certificate for this run.

        uaaml mirror <endpoint> [<node>...] <doc.aml> [--scope node|children|subtree] [--instances-of <type>] [--view <view>]
                     [--depth <n>] [--skip-properties] [--objects-only] [--namespace <uri>]... [--show-server] [--exclude <node>]...
                     [--hierarchy <name>] [--copy] [--preview] [--plan <hierarchy>] [--no-link] [--max-nodes <n>]
                     [--vanished report|mark|remove] [--insecure] [--accept] [-o <out.aml>]
            Take the nodes into the document below an element for the server, each
            with the way to it from the Objects (or Views) folder, with NodeIds, UA
            types (where the document holds them) and current values. --scope says
            how much below each node (default subtree, --depth levels, 0 for all);
            --instances-of takes every instance of the type below each node; --view
            browses a View. Filters leave out properties, variables, other
            namespaces; the Server object is left out unless --show-server.
            Without nodes, the selection kept in the hierarchy is mirrored again.
            A part mirrored before is updated in place, or with --copy mirrored
            into a new hierarchy; nodes the server no longer has are reported, and
            with --vanished marked (attribute NotOnServer) or removed. At most
            --max-nodes nodes are taken (default 2000).
            --preview only counts. A node the document already models (same NodeId,
            in --plan or anywhere) is linked to that planned element with
            refBaseObj, unless --no-link.

        uaaml nodeset <endpoint> [<namespace-uri>] [-o <out.xml>] [--into <doc.aml>] [--instances] [--browse] [--search <dir>]... [--insecure] [--accept]
            The server's namespaces, or the NodeSet of one of them: the file the
            server publishes for it, else rebuilt by browsing its types (with
            --instances also its objects; --browse ignores a published file).
            -o writes it. --into imports it into a document (Annex A, saved to -o
            or in place), with the models it requires that neither --search nor
            the bundled NodeSets provide, fetched from the server too.

        uaaml snapshot [<endpoint>] <doc.aml> [--insecure] [--accept] [-o <out.aml>]
            Read the current value of every bound element and DataVariable. Without
            an endpoint, from every server the document names as a data source
            (--accept then needs an endpoint: the servers a document names are
            not trusted blindly).

        uaaml serve <doc.aml> [--port <n>] [--network] [--simulate]
            Serve the document's instance hierarchies as an OPC UA server until Enter,
            to this computer only. --network offers it to other computers: secured
            endpoints only, and only to clients whose certificate is trusted.
            --simulate lets numbers swing around the document's values and
            booleans toggle, as in a running plant.

        uaaml clients [--trust <thumbprint>] [--distrust <thumbprint>]
            The client certificates the document server refused and those it
            trusts; --trust admits a refused one, --distrust removes a trusted one.

        uaaml doc <doc.aml> [--namespace <uri>] -o <out.html>
            Documentation of an imported OPC UA model as one HTML file: its types with
            their declarations and diagrams, its DataTypes and ReferenceTypes.
            --namespace names the model when the document holds several.

        uaaml diagram <doc.aml> (--type <name|path> | --instance <name|id>) [--depth <n>] -o <out.svg>
            Draw a UA type or an instance as SVG.

        uaaml upgrade <doc.aml> [-o <out.aml>]
            Add the Mandatory children that updated types now declare.

        uaaml link <doc.aml> --from <element> --to <element> [-o <out.aml>]
            Link a VDI 3682 TechnicalResource to a UA object or a ProcessOperator
            to a UA method (elements by name or ID).

        uaaml opcf search <keywords...> [--refresh]
        uaaml opcf download <model-uri> [-o <folder>]
            The NodeSets the OPC Foundation publishes on GitHub (OPCFoundation/UA-Nodeset),
            without an account: search by name or namespace, or download a model with
            the models it requires. The list is kept in %LOCALAPPDATA%\AMLOpcUa\opcfoundation
            and asked of GitHub at most once a day.

        uaaml cloud search <keywords...> [--user <u>]
        uaaml cloud download <id> <folder> [--user <u>]
        uaaml cloud upload <NodeSet.xml> --title <t> --description <d> --copyright <c>
                           [--license MIT|ApacheLicense20|Custom] [--keywords <a,b>] [--doc-url <url>] [--overwrite] [--user <u>]
            Search the UA Cloud Library, download a model with the models it requires,
            or publish one (the OPC Foundation reviews it before it is listed;
            --overwrite replaces your earlier upload of the same model).
            The password comes from UACLOUD_PASSWORD or is asked for; an API key
            from UACLOUD_API_KEY instead.

        uaaml design export <NodeSet.xml|doc.aml> -o <design.xml> [--namespace <uri>] [--name <n>]
        uaaml design compile <design.xml> [-o <folder>] [--include <file>]... [--version v105]
        uaaml design import <design.xml> [--include <file>]... [--into <doc.aml>] [-o <out.aml>] [--search <dir>]...
            ModelDesign, the form the OPC Foundation's ModelCompiler reads and
            writes. export writes a model as a design (its types, declarations,
            fields and references, with the NodeIds kept): from a NodeSet, or
            from a document, whose model is first written back as the nodes
            Annex A made of it (--namespace names it when the document holds
            several); compile
            runs the ModelCompiler over a design and reports the NodeSet it wrote;
            import compiles it and puts the model into a document (Annex A) in one
            go. The ModelCompiler is not part of uaaml; install it with
            "dotnet tool install --global OPCFoundation.Opc.Ua.ModelCompiler.Tool"
            or name it with --compiler. Nothing of its code generation is used.

        uaaml roundtrip <file>... [--search <dir>]... [--inverse] [-o <report.md>]
            Run each file through both mappings and back and report what survives:
            a NodeSet (.xml) UA -> AML (Annex A) -> UA (AML-UA-XSLT rules),
            an AML document (.aml) AML -> UA (AML-UA-XSLT rules) -> AML (Annex A).
            --inverse  a NodeSet goes back through the inverse of Annex A instead,
                       compared node by node with the original

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
                "clients" => ClientsCommand(rest),
                "diagram" => DiagramCommand(rest),
                "doc" => DocCommand(rest),
                "upgrade" => UpgradeCommand(rest),
                "link" => LinkCommand(rest),
                "cloud" => Run(CloudCommand(rest)),
                "opcf" => Run(OpcfCommand(rest)),
                "design" => Run(DesignCommand(rest)),
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
                                   or OpcUaAml.NodeSets.CloudLibraryException or OpcUaAml.NodeSets.OpcFoundationNodeSetsException
                                   or OpcUaAml.Addressing.AddressingException or ModelCompilerException or InvalidDataException)
        {
            return Fail(ex.Message);
        }
        catch (ArgumentException ex)
        {
            return Fail(ex.Message + "\n\n" + Usage);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Xml.XmlException)
        {
            // A file that is missing, locked or not XML: a sentence, not a stack trace.
            return Fail(ex.Message);
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
        if (CaexUpgrade.IsNeeded(target))
        {
            Console.Error.WriteLine($"note: {into} is CAEX {target.CAEXFile.SchemaVersion}; converted to CAEX 3.0 for the OPC UA libraries.");
            target = CaexUpgrade.ToCaex3(target);
        }
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
        var options = Options.Parse(args, valued: new[] { "-o", "--date", "--namespace" }, flags: new[] { "--xslt-compatible", "--annex-a-inverse" });
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
            Mode = options.Has("--annex-a-inverse") ? ExportMode.AnnexAInverse : ExportMode.AmlUaXslt,
            NamespaceUri = options.One("--namespace"),
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
            valued: new[] { "--type", "--name", "--hierarchy", "--optional", "--fill", "-o" },
            flags: new[] { "--allow-abstract" });
        var file = options.SinglePositional("document");
        var doc = Documents.Load(file);
        var typeArg = options.One("--type") ?? throw new ArgumentException("--type is required.");
        var name = options.One("--name") ?? throw new ArgumentException("--name is required.");

        var type = ResolveType(doc, typeArg);
        var optional = options.One("--optional");
        var chosen = optional == null ? new HashSet<string>()
            : optional.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToHashSet();
        var fills = new Dictionary<string, List<PlaceholderFill>>(StringComparer.Ordinal);
        foreach (var fill in options.All("--fill"))
        {
            var eq = fill.IndexOf('=');
            if (eq <= 0) throw new ArgumentException($"--fill '{fill}': give <placeholder>=<name>[:<type>],...");
            var list = fills.TryGetValue(fill[..eq].Trim(), out var known) ? known : fills[fill[..eq].Trim()] = new List<PlaceholderFill>();
            foreach (var entry in fill[(eq + 1)..].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                // The type may be a path with colons of its own; the name ends at the first one.
                var colon = entry.IndexOf(':');
                list.Add(colon < 0 ? new PlaceholderFill(entry) : new PlaceholderFill(entry[..colon], ResolveType(doc, entry[(colon + 1)..])));
            }
        }
        var asked = new HashSet<string>(StringComparer.Ordinal);
        var result = TypeInstantiator.Instantiate(type, name, new InstantiationOptions
        {
            IncludeOptional = p => optional == "all" || chosen.Contains(p),
            FillPlaceholder = p => { asked.Add(p); return fills.TryGetValue(p, out var list) ? list : Array.Empty<PlaceholderFill>(); },
            AllowAbstract = options.Has("--allow-abstract"),
        });
        foreach (var unused in fills.Keys.Where(k => !asked.Contains(k)))
            Console.WriteLine($"The type has no placeholder '{unused}'; it has: {string.Join(", ", asked)}");

        var hierarchyName = options.One("--hierarchy") ?? "OpcUaInstances";
        var ih = doc.CAEXFile.InstanceHierarchy[hierarchyName] ?? doc.CAEXFile.InstanceHierarchy.Append(hierarchyName);
        ih.InternalElement.Insert(result.Instance, asFirst: false);

        Console.WriteLine($"Created '{name}' of {type.Name} in '{hierarchyName}': {result.Included.Count} children.");
        if (result.OmittedOptional.Count > 0)
            Console.WriteLine($"Optional, not created: {string.Join(", ", result.OmittedOptional)}");
        if (result.Filled.Count > 0)
            Console.WriteLine($"For placeholders: {string.Join(", ", result.Filled)}");
        if (result.OmittedPlaceholders.Count > 0)
            Console.WriteLine($"Placeholders, fill with --fill as needed: {string.Join(", ", result.OmittedPlaceholders)}");
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

    /// <summary>A line typed without echo.</summary>
    private static string ReadSecret(string prompt)
    {
        Console.Error.Write(prompt);
        if (Console.IsInputRedirected) return Console.ReadLine() ?? "";
        var text = new System.Text.StringBuilder();
        for (var key = Console.ReadKey(true); key.Key != ConsoleKey.Enter; key = Console.ReadKey(true))
        {
            if (key.Key == ConsoleKey.Backspace) { if (text.Length > 0) text.Length--; }
            else if (!char.IsControl(key.KeyChar)) text.Append(key.KeyChar);
        }
        Console.Error.WriteLine();
        return text.ToString();
    }

    private static OpcUaAml.Server.UaConnectOptions Connect(Options o, string endpoint) => new()
    {
        EndpointUrl = endpoint,
        UseSecurity = !o.Has("--insecure"),
        AcceptUntrustedServerCertificates = o.Has("--accept"),
    };

    private static async Task<int> BrowseCommand(List<string> args)
    {
        var o = Options.Parse(args, valued: Array.Empty<string>(), flags: new[] { "--secure", "--insecure", "--accept" });
        if (o.Positional.Count is < 1 or > 2) throw new ArgumentException("browse needs an endpoint and optionally a node.");
        await using var client = await OpcUaAml.Server.UaClient.ConnectAsync(Connect(o, o.Positional[0]));
        var node = o.Positional.Count == 2 ? OpcUaAml.Addressing.UaNodeAddress.Parse(o.Positional[1], client.NamespaceTable) : null;
        foreach (var item in await client.BrowseAsync(node))
            Console.WriteLine($"{item.NodeClass,-9} {item.BrowseName,-30} {item.Address}");
        return 0;
    }

    private static async Task<int> MirrorCommand(List<string> args)
    {
        var o = Options.Parse(args,
            valued: new[] { "--hierarchy", "--plan", "--depth", "-o", "--scope", "--instances-of", "--view", "--namespace", "--exclude", "--max-nodes", "--vanished" },
            flags: new[] { "--secure", "--insecure", "--accept", "--no-link", "--skip-properties", "--objects-only", "--show-server", "--copy", "--preview" });
        if (o.Positional.Count < 2) throw new ArgumentException("mirror needs an endpoint and a document, and the nodes to take.");
        var docPath = o.Positional[^1];
        var doc = Documents.Load(docPath);
        await using var client = await OpcUaAml.Server.UaClient.ConnectAsync(Connect(o, o.Positional[0]));
        var table = client.NamespaceTable;
        var ihName = o.One("--hierarchy") ?? "OpcUaServer";
        var ih = doc.CAEXFile.InstanceHierarchy[ihName];

        OpcUaAml.Server.MirrorSelection selection;
        var nodes = o.Positional.Skip(1).Take(o.Positional.Count - 2).ToList();
        if (nodes.Count == 0)
        {
            var server = ih == null ? null : OpcUaAml.Server.AddressSpaceMirror.MirroredServer(ih, client);
            selection = (server == null ? null : OpcUaAml.Server.MirrorSelection.ReadFrom(server))
                ?? throw new ArgumentException($"No nodes given, and '{ihName}' keeps no selection for this server.");
        }
        else
        {
            var view = o.One("--view") is { } v ? OpcUaAml.Addressing.UaNodeAddress.Parse(v, table) : null;
            var type = o.One("--instances-of") is { } t ? OpcUaAml.Addressing.UaNodeAddress.Parse(t, table) : null;
            var scope = type != null ? OpcUaAml.Server.MirrorScope.InstancesOf
                : Enum.Parse<OpcUaAml.Server.MirrorScope>(o.One("--scope") ?? "subtree", ignoreCase: true);
            selection = new OpcUaAml.Server.MirrorSelection
            {
                Items = nodes.Select(n => new OpcUaAml.Server.MirrorItem(OpcUaAml.Addressing.UaNodeAddress.Parse(n, table), scope, type, view)).ToList(),
                Depth = int.TryParse(o.One("--depth"), out var d) ? d : 3,
                Filter = new OpcUaAml.Server.MirrorFilter
                {
                    SkipProperties = o.Has("--skip-properties"),
                    ObjectsOnly = o.Has("--objects-only"),
                    HideServer = !o.Has("--show-server"),
                    Namespaces = o.All("--namespace").ToList(),
                },
                Excluded = o.All("--exclude").Select(x => OpcUaAml.Addressing.UaNodeAddress.Parse(x, table)).ToHashSet(),
            };
        }

        var maxNodes = o.One("--max-nodes") is { } m
            ? int.TryParse(m, out var max) && max > 0 ? max : throw new ArgumentException($"--max-nodes: '{m}' is not a positive number.")
            : OpcUaAml.Server.MirrorOptions.DefaultMaxNodes;
        var vanished = o.One("--vanished") is { } vn
            ? Enum.TryParse<OpcUaAml.Server.VanishedNodes>(vn, ignoreCase: true, out var mode) ? mode : throw new ArgumentException($"--vanished: give report, mark or remove, not '{vn}'.")
            : OpcUaAml.Server.VanishedNodes.Report;

        if (o.Has("--preview"))
        {
            var (count, truncated) = await OpcUaAml.Server.AddressSpaceMirror.PreviewAsync(client, selection, maxNodes);
            Console.WriteLine($"{count} element(s){(truncated ? ", stopped at the node limit" : "")}.");
            return 0;
        }

        if (ih != null && o.Has("--copy") && OpcUaAml.Server.AddressSpaceMirror.MirroredServer(ih, client) != null)
        {
            var n = 2;
            while (doc.CAEXFile.InstanceHierarchy[$"{ihName}_{n}"] != null) n++;
            ih = null;
            ihName = $"{ihName}_{n}";
        }
        ih ??= doc.CAEXFile.InstanceHierarchy.Append(ihName);
        var plan = o.One("--plan") is { } planName
            ? doc.CAEXFile.InstanceHierarchy[planName] ?? throw new ArgumentException($"No InstanceHierarchy '{planName}'.")
            : null;
        var result = await OpcUaAml.Server.AddressSpaceMirror.MirrorSelectionAsync(client, selection, ih,
            new OpcUaAml.Server.MirrorOptions { PlannedIn = plan, LinkToPlanned = !o.Has("--no-link"), MaxNodes = maxNodes, Vanished = vanished });
        foreach (var note in result.Notes) Console.Error.WriteLine("note: " + note);
        foreach (var gone in result.Vanished)
            Console.Error.WriteLine((vanished == OpcUaAml.Server.VanishedNodes.Remove ? "removed, not on the server: " : "not on the server: ") + gone);
        Documents.Save(doc, o.One("-o") ?? docPath);
        Console.WriteLine($"{ih.Name}: {result.Nodes} node(s), {result.Created} added, {result.Updated} updated, {result.Typed} typed, "
                          + $"{result.Linked} linked to the plan{(result.Truncated ? ", stopped at the node limit" : "")}.");
        return 0;
    }

    private static async Task<int> NodeSetCommand(List<string> args)
    {
        var o = Options.Parse(args, valued: new[] { "-o", "--into", "--search" }, flags: new[] { "--secure", "--insecure", "--accept", "--instances", "--browse" });
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
        var folder = Directory.CreateTempSubdirectory("uaaml-nodesets-").FullName;
        try
        {
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
        finally
        {
            // The fetched NodeSets are in the document now; the files are not needed.
            try { Directory.Delete(folder, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private static async Task<int> SnapshotCommand(List<string> args)
    {
        var o = Options.Parse(args, valued: new[] { "-o" }, flags: new[] { "--secure", "--insecure", "--accept" });
        if (o.Positional.Count is not (1 or 2)) throw new ArgumentException("snapshot needs a document, optionally after an endpoint.");
        var docPath = o.Positional[^1];
        var doc = Documents.Load(docPath);

        // Without an endpoint: every server the document names as a data
        // source, with the security its description asks for. A document from
        // elsewhere may name any server, so none is trusted blindly.
        if (o.Positional.Count == 1 && o.Has("--accept"))
            throw new ArgumentException("--accept needs an endpoint: the servers a document names are not trusted blindly.");
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
        var o = Options.Parse(args, valued: new[] { "--port" }, flags: new[] { "--network", "--simulate" });
        var doc = Documents.Load(o.SinglePositional("document"));
        var port = int.TryParse(o.One("--port"), out var p) ? p : 48400;
        var network = o.Has("--network");
        await using var host = await OpcUaAml.Server.AmlServerHost.StartAsync(doc, new OpcUaAml.Server.AmlServerOptions { Port = port, Network = network, Simulate = o.Has("--simulate") });
        Console.WriteLine($"Serving {host.Nodes} node(s) at {host.EndpointUrl}"
                          + (network ? " to the network, to trusted clients only (see 'uaaml clients')." : ", to this computer only.")
                          + " Press Enter to stop.");
        Console.ReadLine();
        return 0;
    }

    private static int ClientsCommand(List<string> args)
    {
        var o = Options.Parse(args, valued: new[] { "--trust", "--distrust" }, flags: Array.Empty<string>());
        static OpcUaAml.Server.ClientCertificate Find(IReadOnlyList<OpcUaAml.Server.ClientCertificate> list, string thumbprint)
        {
            var found = list.Where(c => c.Thumbprint.StartsWith(thumbprint, StringComparison.OrdinalIgnoreCase)).ToList();
            return found.Count == 1 ? found[0] : throw new ArgumentException($"No single certificate with thumbprint {thumbprint}.");
        }
        if (o.One("--trust") is { } trust)
            OpcUaAml.Server.AmlServerHost.TrustClient(Find(OpcUaAml.Server.AmlServerHost.RejectedClients(), trust));
        if (o.One("--distrust") is { } distrust)
            OpcUaAml.Server.AmlServerHost.DistrustClient(Find(OpcUaAml.Server.AmlServerHost.TrustedClients(), distrust));
        foreach (var c in OpcUaAml.Server.AmlServerHost.RejectedClients())
            Console.WriteLine($"refused  {c.Thumbprint}  {c.Subject}  (valid until {c.NotAfter:yyyy-MM-dd})");
        foreach (var c in OpcUaAml.Server.AmlServerHost.TrustedClients())
            Console.WriteLine($"trusted  {c.Thumbprint}  {c.Subject}  (valid until {c.NotAfter:yyyy-MM-dd})");
        return 0;
    }

    private static int DocCommand(List<string> args)
    {
        var o = Options.Parse(args, valued: new[] { "-o", "--namespace" }, flags: Array.Empty<string>());
        var doc = Documents.Load(o.SinglePositional("document"));
        var output = o.One("-o") ?? throw new ArgumentException("doc needs -o <out.html>.");
        var models = NamespaceOverview.Of(doc).Select(e => e.NamespaceUri).Where(u => u != "http://opcfoundation.org/UA/").ToList();
        var uri = o.One("--namespace") is { } given
            ? models.FirstOrDefault(u => u == given) ?? models.FirstOrDefault(u => u.Contains(given, StringComparison.OrdinalIgnoreCase))
              ?? throw new ArgumentException($"The document holds no model '{given}'. It holds: {string.Join(", ", models)}.")
            : models.Count == 1 ? models[0]
            : throw new ArgumentException($"The document holds {models.Count} models; name one with --namespace: {string.Join(", ", models)}.");
        File.WriteAllText(output, OpcUaAml.Documentation.ModelDocumentation.Html(doc, uri));
        Console.WriteLine($"{uri} documented in {Path.GetFullPath(output)}");
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

    /// <summary>Where the OPC Foundation's NodeSets from GitHub are kept, the same folder the plugin uses.</summary>
    private static string OpcfFolder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AMLOpcUa", "opcfoundation");

    private static async Task<int> OpcfCommand(List<string> args)
    {
        var o = Options.Parse(args, valued: new[] { "-o" }, flags: new[] { "--refresh" });
        if (o.Positional.Count < 1) throw new ArgumentException("opcf needs 'search <keywords>' or 'download <model-uri>'.");
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        var source = new OpcFoundationNodeSets(http, OpcfFolder);
        var models = await source.ModelsAsync(o.Has("--refresh"));
        switch (o.Positional[0])
        {
            case "search":
                foreach (var m in OpcFoundationNodeSets.Search(models, o.Positional.Skip(1)))
                    Console.WriteLine($"{m.ModelUri,-60} {m.Version,-10} {m.PublicationDate:yyyy-MM-dd}  {m.Path}");
                return 0;
            case "download" when o.Positional.Count == 2:
                var model = OpcFoundationNodeSets.Find(models, o.Positional[1])
                    ?? throw new ArgumentException($"The OPC Foundation's repository has no model '{o.Positional[1]}'; 'opcf search' lists them.");
                var missing = new List<string>();
                var files = await source.DownloadWithDependenciesAsync(model, NodeSetCatalog.Create(Array.Empty<string>()), missing);
                var target = o.One("-o");
                if (target != null) Directory.CreateDirectory(target);
                foreach (var f in files)
                {
                    var written = target == null ? f : Path.Combine(target, Path.GetFileName(f));
                    if (target != null) File.Copy(f, written, overwrite: true);
                    Console.WriteLine(written);
                }
                foreach (var m in missing) Console.Error.WriteLine($"warning: the repository has no model {m}");
                return 0;
            default:
                throw new ArgumentException("opcf needs 'search <keywords>' or 'download <model-uri>'.");
        }
    }

    /// <summary>
    /// ModelDesign, the form the OPC Foundation's ModelCompiler reads: written
    /// from a NodeSet, or compiled back into one (and into a document).
    /// </summary>
    private static async Task<int> DesignCommand(List<string> args)
    {
        var o = Options.Parse(args,
            valued: new[] { "-o", "--namespace", "--name", "--include", "--into", "--search", "--compiler", "--version" },
            flags: new[] { "--keep" });
        if (o.Positional.Count < 1) throw new ArgumentException("design needs 'export', 'compile' or 'import'.");

        switch (o.Positional[0])
        {
            case "export" when o.Positional.Count == 2:
            {
                var output = o.One("-o") ?? throw new ArgumentException("design export needs -o <design.xml>.");
                var options = new ModelDesignOptions { NamespaceUri = o.One("--namespace"), ModelName = o.One("--name") };
                // A document goes out as the nodes Annex A made of it (the
                // inverse export), a NodeSet is read as it is.
                var source = o.Positional[1];
                var result = Path.GetExtension(source).ToLowerInvariant() is ".aml" or ".amlx"
                    ? ModelDesignWriter.From(
                        NodeSetExporter.Export(Documents.Load(source), new NodeSetExportOptions
                        {
                            Mode = ExportMode.AnnexAInverse,
                            NamespaceUri = o.One("--namespace"),
                        }),
                        options)
                    : ModelDesignWriter.FromFile(source, options);
                var identifiers = result.Save(output);
                var root = result.Design.Root!;
                Console.WriteLine($"{(string?)root.Attribute("TargetNamespace")}: "
                    + $"{root.Elements().Count(e => e.Name.LocalName != "Namespaces")} design(s) written to {Path.GetFullPath(output)}");
                Console.WriteLine(identifiers != null
                    ? $"Identifiers: {Path.GetFullPath(identifiers)}"
                    : "No identifier file: the model has no numeric NodeId to keep, so the compiler hands out its own.");
                return 0;
            }

            case "compile" when o.Positional.Count == 2:
            {
                var folder = o.One("-o") ?? Path.Combine(Environment.CurrentDirectory, "compiled");
                var result = await Compile(o, o.Positional[1], folder);
                foreach (var f in result.Files) Console.WriteLine(f);
                Console.WriteLine($"NodeSet: {Path.GetFullPath(result.NodeSetPath)}");
                return 0;
            }

            case "import" when o.Positional.Count == 2:
            {
                var into = o.One("--into");
                var output = o.One("-o") ?? into
                    ?? throw new ArgumentException("Give -o <out.aml>, or --into <doc.aml> to merge into a document.");
                var folder = Directory.CreateTempSubdirectory("uaaml-design-").FullName;
                try
                {
                    var result = await Compile(o, o.Positional[1], folder);
                    Console.WriteLine($"Compiled into {Path.GetFileName(result.NodeSetPath)}");
                    var catalog = NodeSetCatalog.Create(o.All("--search").Append(folder));
                    foreach (var w in catalog.Warnings) Console.Error.WriteLine($"note: {w}");
                    var target = into != null ? Documents.Load(into) : CAEXDocument.New_CAEXDocument();
                    if (CaexUpgrade.IsNeeded(target)) target = CaexUpgrade.ToCaex3(target);
                    var imported = OpcUaImport.ImportInto(target, result.NodeSetPath, catalog,
                        new MergeOptions { ReplaceGeneratedLibraries = !o.Has("--keep") });
                    foreach (var w in imported.Warnings) Console.Error.WriteLine($"warning: {w}");
                    Documents.Save(target, output);
                    Console.WriteLine(imported.Summary);
                    Console.WriteLine($"Written to {Path.GetFullPath(output)}");
                    return 0;
                }
                finally
                {
                    // The compiled NodeSet is in the document now.
                    try { Directory.Delete(folder, recursive: true); }
                    catch (IOException) { }
                    catch (UnauthorizedAccessException) { }
                }
            }

            default:
                throw new ArgumentException("design needs 'export <NodeSet.xml>', 'compile <design.xml>' or 'import <design.xml>'.");
        }
    }

    private static async Task<CompileResult> Compile(Options o, string design, string folder)
    {
        if (ModelCompilerTool.Locate(o.One("--compiler")) is null) throw new ArgumentException(ModelCompilerTool.InstallHint);
        return await ModelCompilerTool.CompileAsync(design, folder, new CompileOptions
        {
            Executable = o.One("--compiler"),
            Included = o.All("--include"),
            SpecificationVersion = o.One("--version"),
        });
    }

    private static async Task<int> CloudCommand(List<string> args)
    {
        var o = Options.Parse(args, valued: new[] { "--user", "--title", "--description", "--copyright", "--license", "--keywords", "--doc-url" },
            flags: new[] { "--overwrite" });
        if (o.Positional.Count < 2) throw new ArgumentException("cloud needs 'search <keywords>', 'download <id> <folder>' or 'upload <NodeSet.xml>'.");
        // Secrets never on the command line, where the process list and the shell history show them.
        var apiKey = Environment.GetEnvironmentVariable("UACLOUD_API_KEY");
        var user = o.One("--user");
        var password = Environment.GetEnvironmentVariable("UACLOUD_PASSWORD");
        if (user != null && password == null && apiKey == null) password = ReadSecret($"Password for {user}: ");
        var client = new CloudLibraryClient(CloudLibraryClient.CreateHttp(), user, password, apiKey);
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
            case "upload" when o.Positional.Count == 2:
                var file = o.Positional[1];
                if (NodeSetInfo.TryRead(file) is not { } info) throw new ArgumentException($"'{file}' is not an OPC UA NodeSet.");
                var answer = await client.UploadAsync(File.ReadAllText(file), new CloudUpload(
                    o.One("--title") ?? throw new ArgumentException("upload needs --title."),
                    o.One("--description") ?? throw new ArgumentException("upload needs --description."),
                    o.One("--copyright") ?? throw new ArgumentException("upload needs --copyright."))
                {
                    License = o.One("--license") ?? "MIT",
                    Keywords = (o.One("--keywords") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
                    DocumentationUrl = o.One("--doc-url") is { } url ? new Uri(url) : null,
                }, o.Has("--overwrite"));
                Console.WriteLine($"{info.Models[0].Model.ModelUri}: {answer}");
                return 0;
            default:
                throw new ArgumentException("cloud needs 'search <keywords>', 'download <id> <folder>' or 'upload <NodeSet.xml>'.");
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
        var o = Options.Parse(args, valued: new[] { "--search", "-o" }, flags: new[] { "--inverse" });
        if (o.Positional.Count == 0) throw new ArgumentException("roundtrip needs at least one file.");
        var report = new System.Text.StringBuilder();
        report.AppendLine("# Round trip report").AppendLine();
        var failed = 0;
        foreach (var file in o.Positional)
        {
            var catalog = NodeSetCatalog.Create(o.All("--search"));
            var isNodeSet = Path.GetExtension(file).Equals(".xml", StringComparison.OrdinalIgnoreCase);
            if (o.Has("--inverse") && !isNodeSet) throw new ArgumentException("--inverse takes NodeSets (.xml).");
            var result = !isNodeSet ? OpcUaAml.Roundtrip.RoundtripRunner.AmlUaAml(file, catalog)
                : o.Has("--inverse") ? OpcUaAml.Roundtrip.RoundtripRunner.UaAmlUaInverse(file, catalog)
                : OpcUaAml.Roundtrip.RoundtripRunner.UaAmlUa(file, catalog);
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
    public static CAEXDocument Load(string path) => AmlFiles.Load(path);

    public static void Save(CAEXDocument doc, string path) => AmlFiles.Save(doc, path);
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
