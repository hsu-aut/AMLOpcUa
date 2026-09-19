// The information model of a running server as NodeSet files, so that its
// types reach AML through the same import as any NodeSet (OPC 10000-83 Annex A)
// and a mirror of the server's objects can refer to them.
//
// A server may publish the NodeSet of a namespace as a file
// (NamespaceMetadataType.NamespaceFile, OPC 10000-5 6.3.13); that file is the
// original and is taken as it is. Otherwise the NodeSet is rebuilt by browsing:
// every type of the namespace with its instance declarations and encodings,
// optionally the namespace's instances too. The stack's NodeSet2 export encodes
// attributes and values; values, DataType definitions, ParentNodeId and
// MethodDeclarationId are read or derived here, because the export leaves them
// out. What a server does not expose (SymbolicName, Documentation, Category,
// nodes no reference leads to, the deprecated type dictionaries) is not in a
// rebuilt file. Tested against DI served from its NodeSet: the import of the
// rebuilt file gives the same AML libraries as the import of the original.

using System.Xml.Linq;
using Opc.Ua;
using Opc.Ua.Client;
using OpcUaAml.NodeSets;

namespace OpcUaAml.Server;

public enum ServerNodeSetSource
{
    /// <summary>The file the server publishes for the namespace (NamespaceFile).</summary>
    NamespaceFile,
    /// <summary>Rebuilt from browsing the server.</summary>
    Browsed,
}

/// <summary>A namespace of a server and what its metadata says about it.</summary>
public sealed record ServerNamespace(string Uri, int Index, string? Version, DateTime? PublicationDate, bool HasFile);

/// <summary>The NodeSet of one namespace, fetched from a server.</summary>
public sealed record ServerNodeSet(
    string ModelUri,
    XDocument Document,
    ServerNodeSetSource Source,
    int NodeCount,
    IReadOnlyList<string> RequiredModels,
    IReadOnlyList<string> Notes);

public sealed class ServerNodeSetOptions
{
    /// <summary>Take the file the server publishes for the namespace when there is one.</summary>
    public bool PreferNamespaceFile { get; init; } = true;

    /// <summary>
    /// Also take the namespace's objects below the Objects folder. Off, the
    /// NodeSet holds types, their declarations and encodings: what becomes AML
    /// libraries. The objects themselves reach a document through the mirror.
    /// </summary>
    public bool IncludeInstances { get; init; }

    /// <summary>At most this many nodes are browsed; beyond, the result says so.</summary>
    public int MaxNodes { get; init; } = 50000;
}

/// <summary>What fetching for an import wrote: the namespace's file first, then the models it needs.</summary>
public sealed record ServerNodeSetFiles(IReadOnlyList<string> Paths, IReadOnlyList<ServerNodeSet> NodeSets);

public static class ServerNodeSets
{
    private const string UaNamespace = "http://opcfoundation.org/UA/";
    private static readonly XNamespace Ua = "http://opcfoundation.org/UA/2011/03/UANodeSet.xsd";
    private const int BrowseBatch = 100;

    /// <summary>The namespaces of the server, with version, publication date and file where it publishes them.</summary>
    public static async Task<IReadOnlyList<ServerNamespace>> ListAsync(UaClient client, CancellationToken ct = default)
    {
        var session = client.Session;
        var metadata = await ReadMetadataAsync(session, ct).ConfigureAwait(false);
        var result = new List<ServerNamespace>();
        for (var i = 0; i < session.NamespaceUris.Count; i++)
        {
            var uri = session.NamespaceUris.GetString((uint)i);
            metadata.TryGetValue(uri, out var m);
            result.Add(new ServerNamespace(uri, i, m?.Version, m?.PublicationDate, m?.File != null));
        }
        return result;
    }

    /// <summary>The NodeSet of one namespace of the server.</summary>
    public static async Task<ServerNodeSet> FetchAsync(UaClient client, string namespaceUri, ServerNodeSetOptions? options = null,
        CancellationToken ct = default)
    {
        options ??= new ServerNodeSetOptions();
        var session = client.Session;
        var index = session.NamespaceUris.GetIndex(namespaceUri);
        if (index < 0) throw new UaConnectionException($"The server has no namespace '{namespaceUri}'.");
        if (index == 0) throw new UaConnectionException("The base namespace of OPC UA comes with the stack; it is not fetched from a server.");

        var metadata = await ReadMetadataAsync(session, ct).ConfigureAwait(false);
        metadata.TryGetValue(namespaceUri, out var own);
        var notes = new List<string>();

        if (options.PreferNamespaceFile && own?.File is { } file)
        {
            try
            {
                var bytes = await ReadFileAsync(session, file, ct).ConfigureAwait(false);
                using var stream = new MemoryStream(bytes);
                var doc = SafeXml.Load(stream);
                var models = doc.Root?.Element(Ua + "Models")?.Elements(Ua + "Model").ToList() ?? new();
                // A file that declares further models could stand in for them (a newer DI, say)
                // in the catalog of later imports; only a file of this model alone is taken.
                if (doc.Root?.Name == Ua + "UANodeSet" && models.Count > 1)
                    notes.Add("The file the server publishes for the namespace declares further models; the NodeSet was rebuilt by browsing.");
                else if (doc.Root?.Name == Ua + "UANodeSet" && models.Any(m => (string?)m.Attribute("ModelUri") == namespaceUri))
                {
                    var required = doc.Root.Element(Ua + "Models")!.Elements(Ua + "Model")
                        .Where(m => (string?)m.Attribute("ModelUri") == namespaceUri)
                        .SelectMany(m => m.Elements(Ua + "RequiredModel")).Select(r => (string)r.Attribute("ModelUri")!).ToList();
                    var count = doc.Root.Elements().Count(e => e.Name.LocalName.StartsWith("UA", StringComparison.Ordinal));
                    return new ServerNodeSet(namespaceUri, doc, ServerNodeSetSource.NamespaceFile, count, required, notes);
                }
                else notes.Add("The file the server publishes for the namespace is not its NodeSet; the NodeSet was rebuilt by browsing.");
            }
            catch (Exception ex) when (ex is ServiceResultException or System.Xml.XmlException)
            {
                notes.Add($"The file the server publishes for the namespace could not be read ({ex.Message}); the NodeSet was rebuilt by browsing.");
            }
        }

        return await BrowseNodeSetAsync(session, namespaceUri, (ushort)index, metadata, options, notes, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Writes the NodeSet of a namespace into <paramref name="folder"/>, and the
    /// NodeSets of the models it requires that <paramref name="catalog"/> lacks
    /// and the server has. Every file written is added to the catalog, so the
    /// first path can be imported at once.
    /// </summary>
    public static async Task<ServerNodeSetFiles> FetchForImportAsync(UaClient client, string namespaceUri, NodeSetCatalog catalog,
        string folder, ServerNodeSetOptions? options = null, CancellationToken ct = default)
    {
        Directory.CreateDirectory(folder);
        var paths = new List<string>();
        var sets = new List<ServerNodeSet>();
        var pending = new Queue<string>(new[] { namespaceUri });
        var seen = new HashSet<string> { namespaceUri };
        var serverUris = client.NamespaceTable.ToHashSet();
        while (pending.Count > 0)
        {
            var uri = pending.Dequeue();
            var set = await FetchAsync(client, uri, options, ct).ConfigureAwait(false);
            var path = Path.Combine(folder, FileNameOf(uri));
            set.Document.Save(path);
            catalog.AddFile(path);
            paths.Add(path);
            sets.Add(set);
            foreach (var r in set.RequiredModels)
            {
                if (r == UaNamespace || !seen.Add(r) || catalog.Find(r) != null || !serverUris.Contains(r)) continue;
                pending.Enqueue(r);
            }
        }
        return new ServerNodeSetFiles(paths, sets);
    }

    private static string FileNameOf(string uri)
    {
        var name = new string(uri.Select(c => char.IsLetterOrDigit(c) || c == '.' ? c : '_').ToArray()).Trim('_');
        return $"{name}.NodeSet2.xml";
    }

    // ---- Rebuilding by browsing ----

    private static async Task<ServerNodeSet> BrowseNodeSetAsync(ISession session, string namespaceUri, ushort index,
        Dictionary<string, Metadata> metadata, ServerNodeSetOptions options, List<string> notes, CancellationToken ct)
    {
        var hierarchical = await SubtypesAsync(session, ReferenceTypeIds.HierarchicalReferences, ct).ConfigureAwait(false);
        hierarchical.Remove(ReferenceTypeIds.HasSubtype);
        var wanted = await CollectAsync(session, index, hierarchical, options, notes, ct).ConfigureAwait(false);
        var nodes = new List<INode>();
        var ids = wanted.Select(id => (ExpandedNodeId)id).ToList();
        for (var i = 0; i < ids.Count; i += 500)
        {
            var batch = await session.NodeCache.FetchNodesAsync(ids.Skip(i).Take(500).ToList(), ct).ConfigureAwait(false);
            nodes.AddRange(batch.OfType<INode>());
        }
        var definitions = await ReadValuesAsync(session, nodes, ct).ConfigureAwait(false);
        var declarations = await MethodDeclarationsAsync(session, nodes, ct).ConfigureAwait(false);

        using var stream = new MemoryStream();
        var exportOptions = new NodeSetExportOptions { ExportValues = true, ExportParentNodeId = true };
        CoreClientUtils.ExportNodesToNodeSet2(session.SystemContext, nodes, stream, exportOptions, null);
        stream.Position = 0;
        var doc = SafeXml.Load(stream);
        var required = Tidy(doc, namespaceUri, wanted, hierarchical, declarations, definitions, session.NamespaceUris, metadata, notes);
        return new ServerNodeSet(namespaceUri, doc, ServerNodeSetSource.Browsed, nodes.Count, required, notes);
    }

    /// <summary>
    /// The nodes of the namespace: types found along HasSubtype from the roots
    /// of the four type hierarchies in every namespace, what the namespace's
    /// nodes hold along hierarchical references, and their encodings; with
    /// instances, also whatever the Objects folder leads to.
    /// </summary>
    private static async Task<HashSet<NodeId>> CollectAsync(ISession session, ushort index, HashSet<NodeId> hierarchical,
        ServerNodeSetOptions options, List<string> notes, CancellationToken ct)
    {
        var wanted = new HashSet<NodeId>();
        var frontier = new List<NodeId> { ObjectTypeIds.BaseObjectType, VariableTypeIds.BaseVariableType, DataTypeIds.BaseDataType, ReferenceTypeIds.References };
        var types = frontier.ToHashSet();
        if (options.IncludeInstances) frontier.Add(ObjectIds.ObjectsFolder);
        var visited = frontier.ToHashSet();
        var limited = false;

        while (frontier.Count > 0)
        {
            var refs = await BrowseForwardAsync(session, frontier, ct).ConfigureAwait(false);
            var next = new List<NodeId>();
            for (var j = 0; j < frontier.Count; j++)
            {
                var source = frontier[j];
                var own = source.NamespaceIndex == index;
                var isType = types.Contains(source);
                foreach (var r in refs[j])
                {
                    if (r.NodeId.ServerIndex != 0) continue;
                    var target = ExpandedNodeId.ToNodeId(r.NodeId, session.NamespaceUris);
                    if (target == null || visited.Contains(target)) continue;
                    var subtype = isType && r.ReferenceTypeId == ReferenceTypeIds.HasSubtype;
                    var follow = subtype
                        || (own && (hierarchical.Contains(r.ReferenceTypeId) || r.ReferenceTypeId == ReferenceTypeIds.HasEncoding))
                        || (options.IncludeInstances && !isType && hierarchical.Contains(r.ReferenceTypeId));
                    if (!follow) continue;
                    if (visited.Count >= options.MaxNodes) { limited = true; continue; }
                    visited.Add(target);
                    if (subtype) types.Add(target);
                    if (target.NamespaceIndex == index) wanted.Add(target);
                    next.Add(target);
                }
            }
            frontier = next;
        }
        if (limited) notes.Add($"Browsing stopped at {options.MaxNodes} nodes; the NodeSet is incomplete.");
        return wanted;
    }

    /// <summary>A type and all its subtypes, in every namespace of the server.</summary>
    private static async Task<HashSet<NodeId>> SubtypesAsync(ISession session, NodeId root, CancellationToken ct)
    {
        var result = new HashSet<NodeId> { root };
        var frontier = new List<NodeId> { root };
        while (frontier.Count > 0)
        {
            var refs = await BrowseForwardAsync(session, frontier, ct, ReferenceTypeIds.HasSubtype).ConfigureAwait(false);
            frontier = refs.SelectMany(r => r).Select(r => ExpandedNodeId.ToNodeId(r.NodeId, session.NamespaceUris))
                .Where(n => n != null && result.Add(n)).ToList();
        }
        return result;
    }

    /// <summary>
    /// The node cache reads neither values nor DataType definitions; declarations
    /// carry defaults and method arguments in their values.
    /// </summary>
    private static async Task<Dictionary<NodeId, Definition>> ReadValuesAsync(ISession session, List<INode> nodes, CancellationToken ct)
    {
        var result = new Dictionary<NodeId, Definition>();
        var dataTypes = nodes.OfType<DataTypeNode>().ToList();
        var definitions = await ReadAsync(session, dataTypes.Select(n => ExpandedNodeId.ToNodeId(n.NodeId, session.NamespaceUris)).ToList(),
            Attributes.DataTypeDefinition, ct).ConfigureAwait(false);
        for (var j = 0; j < dataTypes.Count; j++)
        {
            if (!StatusCode.IsGood(definitions[j].StatusCode) || definitions[j].Value is not ExtensionObject { Body: var body }) continue;
            var id = ExpandedNodeId.ToNodeId(dataTypes[j].NodeId, session.NamespaceUris);
            // Named values are an enumeration's or, below an unsigned integer or OptionSet, an OptionSet's bits.
            var optionSet = body is EnumDefinition && !await IsSubtypeAsync(session, id, DataTypeIds.Enumeration, ct).ConfigureAwait(false);
            result[id] = new Definition(dataTypes[j].BrowseName, body as IEncodeable, optionSet);
        }

        var variables = nodes.Where(n => n is VariableNode or VariableTypeNode).ToList();
        for (var i = 0; i < variables.Count; i += 500)
        {
            var batch = variables.Skip(i).Take(500).ToList();
            var values = await ReadAsync(session, batch.Select(n => ExpandedNodeId.ToNodeId(n.NodeId, session.NamespaceUris)).ToList(),
                Attributes.Value, ct).ConfigureAwait(false);
            for (var j = 0; j < batch.Count; j++)
            {
                if (StatusCode.IsBad(values[j].StatusCode) || values[j].Value == null) continue;
                if (batch[j] is VariableNode v) v.Value = values[j].WrappedValue;
                else if (batch[j] is VariableTypeNode vt) vt.Value = values[j].WrappedValue;
            }
        }
        return result;
    }

    private sealed record Definition(QualifiedName Name, IEncodeable? Body, bool IsOptionSet);

    private static async Task<bool> IsSubtypeAsync(ISession session, NodeId type, NodeId baseType, CancellationToken ct)
    {
        var seen = new HashSet<NodeId>();
        for (NodeId? t = type; t != null && seen.Add(t); t = await SupertypeAsync(session, t, ct).ConfigureAwait(false))
            if (t == baseType) return true;
        return false;
    }

    /// <summary>
    /// MethodDeclarationId of the methods below instances and declarations: the
    /// method of the same BrowseName in the type of the node holding it, or in
    /// a supertype. It is no attribute, so the server cannot be asked for it.
    /// </summary>
    private static async Task<Dictionary<NodeId, NodeId>> MethodDeclarationsAsync(ISession session, List<INode> nodes, CancellationToken ct)
    {
        var result = new Dictionary<NodeId, NodeId>();
        var methods = nodes.OfType<MethodNode>().ToList();
        var methodIds = methods.Select(m => ExpandedNodeId.ToNodeId(m.NodeId, session.NamespaceUris)).ToList();
        // The node holding each method, and the type of that node when it is an object or variable.
        var holders = (await BrowseForwardAsync(session, methodIds, ct, ReferenceTypeIds.HasComponent, BrowseDirection.Inverse).ConfigureAwait(false))
            .Select(refs => refs.FirstOrDefault(r => r.NodeClass is NodeClass.Object or NodeClass.Variable))
            .Select(r => r == null ? null : ExpandedNodeId.ToNodeId(r.NodeId, session.NamespaceUris)).ToList();
        var holderIds = holders.Where(h => h != null).Distinct().ToList();
        var typeRefs = await BrowseForwardAsync(session, holderIds!, ct, ReferenceTypeIds.HasTypeDefinition).ConfigureAwait(false);
        var typeOf = holderIds.Zip(typeRefs).Where(x => x.Second.Count > 0)
            .ToDictionary(x => x.First!, x => ExpandedNodeId.ToNodeId(x.Second[0].NodeId, session.NamespaceUris));
        var methodsOfType = new Dictionary<NodeId, Dictionary<string, NodeId>>();
        for (var m = 0; m < methods.Count; m++)
        {
            var method = methods[m];
            var id = methodIds[m];
            if (holders[m] is not { } holder || !typeOf.TryGetValue(holder, out var firstType)) continue;
            var seen = new HashSet<NodeId>();
            for (var type = firstType;
                 type != null && !NodeId.IsNull(type) && seen.Add(type);
                 type = await SupertypeAsync(session, type, ct).ConfigureAwait(false))
            {
                if (!methodsOfType.TryGetValue(type, out var declared))
                {
                    var children = (await BrowseForwardAsync(session, new List<NodeId> { type }, ct, ReferenceTypeIds.HasComponent).ConfigureAwait(false))[0];
                    methodsOfType[type] = declared = children.Where(c => c.NodeClass == NodeClass.Method)
                        .GroupBy(c => c.BrowseName.Name).ToDictionary(g => g.Key, g => ExpandedNodeId.ToNodeId(g.First().NodeId, session.NamespaceUris));
                }
                if (declared.TryGetValue(method.BrowseName.Name, out var declaration))
                {
                    if (declaration != id) result[id] = declaration;
                    break;
                }
            }
        }
        return result;
    }

    private static async Task<NodeId?> SupertypeAsync(ISession session, NodeId type, CancellationToken ct)
    {
        var descriptions = new BrowseDescriptionCollection
        {
            new BrowseDescription
            {
                NodeId = type,
                BrowseDirection = BrowseDirection.Inverse,
                ReferenceTypeId = ReferenceTypeIds.HasSubtype,
                IncludeSubtypes = false,
                ResultMask = (uint)BrowseResultMask.All,
            },
        };
        var response = await session.BrowseAsync(null, null, 0, descriptions, ct).ConfigureAwait(false);
        var first = response.Results[0].References.FirstOrDefault();
        return first == null ? null : ExpandedNodeId.ToNodeId(first.NodeId, session.NamespaceUris);
    }

    private static async Task<List<ReferenceDescriptionCollection>> BrowseForwardAsync(ISession session, List<NodeId> nodes, CancellationToken ct,
        NodeId? referenceType = null, BrowseDirection direction = BrowseDirection.Forward)
    {
        var result = new List<ReferenceDescriptionCollection>();
        for (var i = 0; i < nodes.Count; i += BrowseBatch)
        {
            var descriptions = new BrowseDescriptionCollection(nodes.Skip(i).Take(BrowseBatch).Select(n => new BrowseDescription
            {
                NodeId = n,
                BrowseDirection = direction,
                ReferenceTypeId = referenceType ?? ReferenceTypeIds.References,
                IncludeSubtypes = true,
                NodeClassMask = 0,
                ResultMask = (uint)(BrowseResultMask.ReferenceTypeId | BrowseResultMask.IsForward | BrowseResultMask.BrowseName | BrowseResultMask.NodeClass),
            }));
            var response = await session.BrowseAsync(null, null, 0, descriptions, ct).ConfigureAwait(false);
            foreach (var r in response.Results)
            {
                var all = new ReferenceDescriptionCollection(r.References);
                var continuation = r.ContinuationPoint;
                while (continuation != null && continuation.Length > 0)
                {
                    if (all.Count > UaClient.MaxReferencesPerNode)
                        throw new UaConnectionException($"The server keeps sending references of one node (more than {UaClient.MaxReferencesPerNode}).");
                    var next = await session.BrowseNextAsync(null, false, new ByteStringCollection { continuation }, ct).ConfigureAwait(false);
                    all.AddRange(next.Results[0].References);
                    continuation = next.Results[0].ContinuationPoint;
                }
                result.Add(all);
            }
        }
        return result;
    }

    /// <summary>
    /// Makes the exported file a NodeSet of one model the way NodeSets are
    /// usually written: the Model with version and publication date from the
    /// server's metadata and a RequiredModel per other namespace used; no
    /// references to nodes of the namespace that were not exported; HasSubtype
    /// and HasEncoding only at the subtype and the encoding, HasInterface only
    /// at its source; ParentNodeId and
    /// MethodDeclarationId. Returns the required model URIs.
    /// </summary>
    private static List<string> Tidy(XDocument doc, string namespaceUri, HashSet<NodeId> exported, HashSet<NodeId> hierarchical,
        Dictionary<NodeId, NodeId> declarations, Dictionary<NodeId, Definition> definitions, NamespaceTable serverTable,
        Dictionary<string, Metadata> metadata, List<string> notes)
    {
        var root = doc.Root!;
        var uris = root.Element(Ua + "NamespaceUris");
        if (uris == null) { uris = new XElement(Ua + "NamespaceUris"); root.AddFirst(uris); }
        var fileTable = new NamespaceTable();
        foreach (var uri in uris.Elements(Ua + "Uri").Select(u => u.Value)) fileTable.Append(uri);
        var aliases = root.Element(Ua + "Aliases")?.Elements(Ua + "Alias").ToDictionary(a => (string)a.Attribute("Alias")!, a => a.Value)
            ?? new Dictionary<string, string>();

        NodeId Resolve(string text) => NodeId.Parse(aliases.TryGetValue(text.Trim(), out var a) ? a : text.Trim());
        NodeId? InFile(NodeId serverId)
        {
            if (serverId.NamespaceIndex == 0) return serverId;
            var uri = serverTable.GetString(serverId.NamespaceIndex);
            if (uri == null) return null;
            var i = fileTable.GetIndex(uri);
            if (i < 0) { i = (int)fileTable.Append(uri); uris.Add(new XElement(Ua + "Uri", uri)); }
            return new NodeId(serverId.Identifier, (ushort)i);
        }

        var nodes = root.Elements().Where(e => e.Name.LocalName.StartsWith("UA", StringComparison.Ordinal) && e.Attribute("NodeId") != null)
            .ToDictionary(e => NodeId.Parse((string)e.Attribute("NodeId")!));
        var exportedInFile = exported.Select(InFile).Where(n => n != null).ToHashSet();
        var hierarchicalInFile = hierarchical.Select(InFile).Where(n => n != null).ToHashSet();
        var own = (ushort)fileTable.GetIndex(namespaceUri);

        var refs = nodes.SelectMany(n => (n.Value.Element(Ua + "References")?.Elements(Ua + "Reference") ?? Enumerable.Empty<XElement>())
            .Select(r => (Node: n.Key, Element: r, Type: Resolve((string)r.Attribute("ReferenceType")!), Target: Resolve(r.Value),
                Forward: (string?)r.Attribute("IsForward") != "false"))).ToList();

        var dropped = 0;
        foreach (var r in refs.Where(r => r.Target.NamespaceIndex == own && !exportedInFile.Contains(r.Target)))
        {
            r.Element.Remove();
            dropped++;
        }
        if (dropped > 0) notes.Add($"{dropped} reference(s) to nodes of the namespace outside the exported ones were left out.");

        var present = refs.Where(r => r.Element.Parent != null).Select(r => (r.Node, r.Type, r.Target, r.Forward)).ToHashSet();
        var atTarget = new[] { ReferenceTypeIds.HasSubtype, ReferenceTypeIds.HasEncoding };
        var atSource = new[] { ReferenceTypeIds.HasInterface };
        foreach (var r in refs.Where(r => r.Element.Parent != null && (atTarget.Contains(r.Type) || atSource.Contains(r.Type))))
        {
            if (!present.Contains((r.Target, r.Type, r.Node, !r.Forward))) continue;
            if (r.Forward == atTarget.Contains(r.Type))
            {
                r.Element.Remove();
                present.Remove((r.Node, r.Type, r.Target, r.Forward));
            }
        }

        foreach (var (id, definition) in definitions)
        {
            if (InFile(id) is not { } d || !nodes.TryGetValue(d, out var element) || element.Element(Ua + "Definition") != null) continue;
            element.Add(DefinitionElement(definition));
        }

        // An empty Description of an Argument is the same as none.
        foreach (var d in root.Descendants(XName.Get("Description", "http://opcfoundation.org/UA/2008/02/Types.xsd"))
                     .Where(d => d.Parent?.Name.LocalName == "Argument" && !d.HasElements && d.Value.Length == 0).ToList())
            d.Remove();

        var preferred = new[] { ReferenceTypeIds.HasComponent, ReferenceTypeIds.HasProperty, ReferenceTypeIds.HasOrderedComponent };
        foreach (var (id, element) in nodes)
        {
            if (element.Name.LocalName is not ("UAObject" or "UAVariable" or "UAMethod") || element.Attribute("ParentNodeId") != null) continue;
            var parents = refs.Where(r => r.Node == id && r.Element.Parent != null && !r.Forward && hierarchicalInFile.Contains(r.Type)
                    && r.Type != ReferenceTypeIds.Organizes && nodes.ContainsKey(r.Target))
                .OrderBy(r => preferred.Contains(r.Type) ? 0 : 1).ToList();
            if (parents.Count > 0) element.SetAttributeValue("ParentNodeId", parents[0].Target.ToString());
        }
        foreach (var (method, declaration) in declarations)
        {
            if (InFile(method) is { } m && nodes.TryGetValue(m, out var element) && element.Attribute("MethodDeclarationId") == null
                && InFile(declaration) is { } d)
                element.SetAttributeValue("MethodDeclarationId", d.ToString());
        }

        XElement DefinitionElement(Definition definition)
        {
            var nameIndex = definition.Name.NamespaceIndex == 0 ? 0 : fileTable.GetIndex(serverTable.GetString(definition.Name.NamespaceIndex));
            var e = new XElement(Ua + "Definition", new XAttribute("Name", nameIndex <= 0 ? definition.Name.Name : $"{nameIndex}:{definition.Name.Name}"));
            string TypeText(NodeId serverId)
            {
                var id = (InFile(serverId) ?? serverId).ToString();
                return aliases.FirstOrDefault(a => a.Value == id).Key ?? id;
            }
            static XElement? Text(string name, LocalizedText? text) =>
                text == null || LocalizedText.IsNullOrEmpty(text) ? null
                    : new XElement(Ua + name, string.IsNullOrEmpty(text.Locale) ? null : new XAttribute("Locale", text.Locale), text.Text);
            switch (definition.Body)
            {
                case StructureDefinition s:
                    if (s.StructureType is StructureType.Union or StructureType.UnionWithSubtypedValues) e.Add(new XAttribute("IsUnion", "true"));
                    foreach (var f in s.Fields)
                    {
                        e.Add(new XElement(Ua + "Field",
                            new XAttribute("Name", f.Name),
                            new XAttribute("DataType", TypeText(f.DataType)),
                            f.ValueRank != ValueRanks.Scalar ? new XAttribute("ValueRank", f.ValueRank) : null,
                            f.ArrayDimensions?.Count > 0 ? new XAttribute("ArrayDimensions", string.Join(",", f.ArrayDimensions)) : null,
                            f.MaxStringLength > 0 ? new XAttribute("MaxStringLength", f.MaxStringLength) : null,
                            f.IsOptional ? new XAttribute("IsOptional", "true") : null,
                            Text("Description", f.Description)));
                    }
                    break;
                case EnumDefinition en:
                    if (definition.IsOptionSet) e.Add(new XAttribute("IsOptionSet", "true"));
                    foreach (var f in en.Fields)
                    {
                        e.Add(new XElement(Ua + "Field",
                            new XAttribute("Name", f.Name),
                            new XAttribute("Value", f.Value),
                            f.DisplayName?.Text is { } shown && shown != f.Name ? Text("DisplayName", f.DisplayName) : null,
                            Text("Description", f.Description)));
                    }
                    break;
            }
            return e;
        }

        var required = fileTable.ToArray().Where(u => u != namespaceUri && u != UaNamespace).Prepend(UaNamespace).ToList();
        root.Element(Ua + "Models")?.Remove();
        var model = new XElement(Ua + "Model", new XAttribute("ModelUri", namespaceUri));
        AddVersion(model, metadata, namespaceUri);
        foreach (var r in required)
        {
            var element = new XElement(Ua + "RequiredModel", new XAttribute("ModelUri", r));
            AddVersion(element, metadata, r);
            model.Add(element);
        }
        var models = new XElement(Ua + "Models", model);
        (root.Element(Ua + "ServerUris") ?? uris).AddAfterSelf(models);
        return required;
    }

    private static void AddVersion(XElement model, Dictionary<string, Metadata> metadata, string uri)
    {
        if (!metadata.TryGetValue(uri, out var m)) return;
        if (!string.IsNullOrEmpty(m.Version)) model.Add(new XAttribute("Version", m.Version));
        if (m.PublicationDate is { } date && date > DateTime.MinValue)
            model.Add(new XAttribute("PublicationDate", date.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ")));
    }

    // ---- Namespace metadata and files ----

    private sealed record Metadata(string Uri, string? Version, DateTime? PublicationDate, NodeId? File);

    /// <summary>The NamespaceMetadata objects below Server.Namespaces, by namespace URI.</summary>
    private static async Task<Dictionary<string, Metadata>> ReadMetadataAsync(ISession session, CancellationToken ct)
    {
        var result = new Dictionary<string, Metadata>();
        var objects = (await BrowseForwardAsync(session, new List<NodeId> { ObjectIds.Server_Namespaces }, ct).ConfigureAwait(false))[0]
            .Select(r => ExpandedNodeId.ToNodeId(r.NodeId, session.NamespaceUris)).Where(n => n != null).ToList();
        if (objects.Count == 0) return result;
        var children = await BrowseForwardAsync(session, objects, ct).ConfigureAwait(false);
        var browseNames = new Dictionary<NodeId, string>();
        var allChildren = children.SelectMany(c => c).Select(r => ExpandedNodeId.ToNodeId(r.NodeId, session.NamespaceUris)).Where(n => n != null).Distinct().ToList();
        var names = await ReadAsync(session, allChildren, Attributes.BrowseName, ct).ConfigureAwait(false);
        for (var i = 0; i < allChildren.Count; i++)
            if (names[i].Value is QualifiedName q) browseNames[allChildren[i]] = q.Name;

        for (var i = 0; i < objects.Count; i++)
        {
            NodeId? Child(string name) => children[i].Select(r => ExpandedNodeId.ToNodeId(r.NodeId, session.NamespaceUris))
                .FirstOrDefault(n => n != null && browseNames.TryGetValue(n, out var b) && b == name);
            var uriNode = Child("NamespaceUri");
            if (uriNode == null) continue;
            var props = new[] { uriNode, Child("NamespaceVersion"), Child("NamespacePublicationDate") };
            var values = await ReadAsync(session, props.Where(p => p != null).ToList()!, Attributes.Value, ct).ConfigureAwait(false);
            var map = props.Where(p => p != null).Zip(values).ToDictionary(x => x.First!, x => x.Second.Value);
            if (map[uriNode] is not string uri) continue;
            var version = props[1] != null ? map[props[1]!] as string : null;
            var date = props[2] != null && map[props[2]!] is DateTime d ? d : (DateTime?)null;
            result[uri] = new Metadata(uri, version, date, Child("NamespaceFile"));
        }
        return result;
    }

    private static async Task<DataValueCollection> ReadAsync(ISession session, List<NodeId> nodes, uint attribute, CancellationToken ct)
    {
        if (nodes.Count == 0) return new DataValueCollection();
        var request = new ReadValueIdCollection(nodes.Select(n => new ReadValueId { NodeId = n, AttributeId = attribute }));
        var response = await session.ReadAsync(null, 0, TimestampsToReturn.Neither, request, ct).ConfigureAwait(false);
        return response.Results;
    }

    /// <summary>The largest NodeSet file taken from a server; the largest companion specifications have a few MB.</summary>
    public const long MaxFileBytes = 64L << 20;

    /// <summary>Reads a FileType object: Open for reading, Read until the end, Close (OPC 10000-5 C.2).</summary>
    private static async Task<byte[]> ReadFileAsync(ISession session, NodeId file, CancellationToken ct)
    {
        var methods = (await BrowseForwardAsync(session, new List<NodeId> { file }, ct).ConfigureAwait(false))[0]
            .Select(r => ExpandedNodeId.ToNodeId(r.NodeId, session.NamespaceUris)).Where(n => n != null).ToList();
        var names = await ReadAsync(session, methods, Attributes.BrowseName, ct).ConfigureAwait(false);
        NodeId Method(string name) => methods.Where((_, i) => names[i].Value is QualifiedName q && q.Name == name).FirstOrDefault()
            ?? throw new ServiceResultException(StatusCodes.BadNotSupported, $"The file object has no method {name}.");

        var open = await CallAsync(session, file, Method("Open"), ct, (byte)1).ConfigureAwait(false);
        var handle = (uint)open[0];
        try
        {
            using var content = new MemoryStream();
            while (true)
            {
                var read = await CallAsync(session, file, Method("Read"), ct, handle, 1 << 20).ConfigureAwait(false);
                if (read[0] is not byte[] chunk || chunk.Length == 0) break;
                content.Write(chunk);
                if (content.Length > MaxFileBytes)
                    throw new ServiceResultException(StatusCodes.BadEncodingLimitsExceeded, $"The file is larger than {MaxFileBytes >> 20} MB.");
            }
            return content.ToArray();
        }
        finally
        {
            // A failing Close must not hide why reading stopped.
            try { await CallAsync(session, file, Method("Close"), ct, handle).ConfigureAwait(false); }
            catch (ServiceResultException) { /* the server drops the handle with the session */ }
        }
    }

    private static async Task<IList<object>> CallAsync(ISession session, NodeId objectId, NodeId methodId, CancellationToken ct, params object[] args)
    {
        var request = new CallMethodRequestCollection
        {
            new CallMethodRequest { ObjectId = objectId, MethodId = methodId, InputArguments = new VariantCollection(args.Select(a => new Variant(a))) },
        };
        var response = await session.CallAsync(null, request, ct).ConfigureAwait(false);
        var result = response.Results[0];
        if (StatusCode.IsBad(result.StatusCode)) throw new ServiceResultException(result.StatusCode);
        return result.OutputArguments.Select(v => v.Value).ToList();
    }
}
