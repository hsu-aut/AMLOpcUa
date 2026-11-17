// A running server checked against the types its instances claim (OPC
// 10000-3 6.4 InstanceDeclarations, 6.4.4 ModellingRules): for every Object
// and Variable below a start node, the declarations of its type and of the
// type's supertypes, and those a parent's declaration holds below the child
// (StationType declares ParameterSet/CycleTime). Found: a Mandatory child that
// is missing, a child of another NodeClass, of another type or holding
// another data type than declared (a subtype is fine), an instance of an
// abstract type, a MandatoryPlaceholder without any child. What commissioning
// asks of a PLC: does it implement the companion specification it claims?

using Opc.Ua;
using OpcUaAml.Addressing;
using OpcUaAml.Server;

namespace OpcUaAml.Checks;

/// <summary>The findings, how many nodes were checked, and whether the limit stopped the check.</summary>
public sealed record ServerCheckResult(IReadOnlyList<Finding> Findings, int Checked, bool Truncated);

public static class ServerChecker
{
    /// <summary>
    /// Checks the instances below <paramref name="start"/> (the start itself
    /// included), or below the Objects folder without the server's own nodes.
    /// </summary>
    public static Task<ServerCheckResult> CheckAsync(UaClient client, UaNodeAddress? start = null, int limit = 20000, CancellationToken ct = default) =>
        new Run(client, limit, ct).CheckAsync(start);

    private sealed record Child(NodeId Id, QualifiedName Name, NodeClass Class, NodeId? TypeDefinition);

    private sealed record Declaration(NodeId Node, QualifiedName Name, NodeClass Class, NodeId? TypeDefinition, NodeId? DataType, NodeId Rule, string Owner);

    private sealed class Run(UaClient client, int limit, CancellationToken ct)
    {
        private readonly Opc.Ua.Client.ISession _s = client.Session;
        private readonly List<Finding> _findings = new();
        private readonly Dictionary<NodeId, IReadOnlyList<Declaration>> _ofType = new();
        private readonly Dictionary<NodeId, IReadOnlyList<Declaration>> _below = new();
        private readonly Dictionary<NodeId, string> _names = new();
        private readonly Dictionary<NodeId, bool> _abstract = new();
        private readonly Dictionary<NodeId, NodeId?> _super = new();
        private int _checked;
        private bool _truncated;

        public async Task<ServerCheckResult> CheckAsync(UaNodeAddress? start)
        {
            if (start == null)
            {
                // The server's own nodes (Server object, diagnostics) are the stack's, not the model's.
                foreach (var c in await ChildrenAsync(ObjectIds.ObjectsFolder))
                    if (c.Id.NamespaceIndex != 0) await CheckAsync(c, c.Name.Name, null);
            }
            else
            {
                var id = client.ToNodeId(start);
                await CheckAsync(await DescribeAsync(id), (await NameOfAsync(id)), null);
            }
            return new ServerCheckResult(_findings, _checked, _truncated);
        }

        private async Task CheckAsync(Child node, string path, IReadOnlyList<Declaration>? below)
        {
            if (node.Class is not (NodeClass.Object or NodeClass.Variable)) return;
            if (_checked >= limit) { _truncated = true; return; }
            _checked++;
            ct.ThrowIfCancellationRequested();

            var declared = new List<Declaration>();
            if (node.TypeDefinition == null)
                Add(Rules.ServerNoTypeDefinition, Severity.Warning, path, node.Id, FindingTexts.NoTypeDefinition);
            else
            {
                if (await IsAbstractAsync(node.TypeDefinition))
                    Add(Rules.ServerAbstractType, Severity.Error, path, node.Id, FindingTexts.AbstractType, await NameOfAsync(node.TypeDefinition));
                declared.AddRange(await DeclarationsOfTypeAsync(node.TypeDefinition));
            }
            // What the parent's declaration holds below this child, where the type does not say it itself.
            if (below != null)
                foreach (var d in below)
                    if (!declared.Any(x => x.Name == d.Name)) declared.Add(d);

            var children = await ChildrenAsync(node.Id);
            var dataTypes = await DataTypesAsync(children.Where(c => c.Class == NodeClass.Variable).Select(c => c.Id).ToList());
            var matched = new Dictionary<Child, Declaration>();
            foreach (var d in declared.Where(d => d.Rule != ObjectIds.ModellingRule_MandatoryPlaceholder && d.Rule != ObjectIds.ModellingRule_OptionalPlaceholder))
            {
                var c = children.FirstOrDefault(x => x.Name == d.Name);
                if (c == null)
                {
                    if (d.Rule == ObjectIds.ModellingRule_Mandatory)
                        Add(Rules.ServerMissingMandatory, Severity.Error, path, node.Id, FindingTexts.MissingMandatory, d.Name.Name, d.Owner);
                    continue;
                }
                matched[c] = d;
                var at = path + "/" + c.Name.Name;
                if (c.Class != d.Class)
                {
                    Add(Rules.ServerWrongNodeClass, Severity.Error, at, c.Id, FindingTexts.WrongNodeClass, c.Name.Name, c.Class.ToString(), d.Owner, d.Class.ToString());
                    continue;
                }
                if (d.TypeDefinition != null && c.TypeDefinition != null && !await IsSubtypeAsync(c.TypeDefinition, d.TypeDefinition))
                    Add(Rules.ServerWrongTypeDefinition, Severity.Error, at, c.Id, FindingTexts.WrongTypeDefinition,
                        c.Name.Name, await NameOfAsync(c.TypeDefinition), d.Owner, await NameOfAsync(d.TypeDefinition));
                if (c.Class == NodeClass.Variable && d.DataType != null && dataTypes.TryGetValue(c.Id, out var dataType) && dataType != null
                    && !await IsSubtypeAsync(dataType, d.DataType))
                    Add(Rules.ServerWrongDataType, Severity.Error, at, c.Id, FindingTexts.WrongDataType,
                        c.Name.Name, await NameOfAsync(dataType), d.Owner, await NameOfAsync(d.DataType));
            }
            foreach (var d in declared.Where(d => d.Rule == ObjectIds.ModellingRule_MandatoryPlaceholder))
            {
                var any = false;
                foreach (var c in children.Where(c => !matched.ContainsKey(c) && c.TypeDefinition != null))
                    if (d.TypeDefinition == null || await IsSubtypeAsync(c.TypeDefinition!, d.TypeDefinition)) { any = true; break; }
                if (!any)
                    Add(Rules.ServerMissingMandatoryPlaceholder, Severity.Error, path, node.Id, FindingTexts.MissingMandatoryPlaceholderOfType,
                        d.Owner, d.Name.Name, d.TypeDefinition != null ? await NameOfAsync(d.TypeDefinition) : "BaseObjectType");
            }

            foreach (var c in children)
                await CheckAsync(c, path + "/" + c.Name.Name, matched.TryGetValue(c, out var d) ? await DeclarationsBelowAsync(d) : null);
        }

        private void Add(string rule, Severity severity, string path, NodeId id, string format, params string[] args) =>
            _findings.Add(Finding.Of(rule, severity, path, client.FromExpanded(id).ToString(), format, args));

        /// <summary>The declarations of a type and its supertypes; a subtype's own override those of the same BrowseName.</summary>
        private async Task<IReadOnlyList<Declaration>> DeclarationsOfTypeAsync(NodeId type)
        {
            if (_ofType.TryGetValue(type, out var known)) return known;
            var all = new List<Declaration>();
            var seen = new HashSet<NodeId>();
            for (NodeId? t = type; t != null && !NodeId.IsNull(t) && seen.Add(t); t = await SuperOfAsync(t))
                foreach (var d in await DeclarationsOfAsync(t, await NameOfAsync(t)))
                    if (!all.Any(x => x.Name == d.Name)) all.Add(d);
            return _ofType[type] = all;
        }

        /// <summary>What a declaration holds below it, for the child that it stands for.</summary>
        private async Task<IReadOnlyList<Declaration>> DeclarationsBelowAsync(Declaration d)
        {
            if (_below.TryGetValue(d.Node, out var known)) return known;
            return _below[d.Node] = await DeclarationsOfAsync(d.Node, d.Owner);
        }

        /// <summary>The children of a node that have a ModellingRule: its instance declarations.</summary>
        private async Task<IReadOnlyList<Declaration>> DeclarationsOfAsync(NodeId parent, string owner)
        {
            var children = await ChildrenAsync(parent);
            if (children.Count == 0) return [];
            var browse = new BrowseDescriptionCollection(children.Select(c => new BrowseDescription
            {
                NodeId = c.Id, BrowseDirection = BrowseDirection.Forward, ReferenceTypeId = ReferenceTypeIds.HasModellingRule,
                IncludeSubtypes = false, NodeClassMask = (uint)NodeClass.Object, ResultMask = (uint)BrowseResultMask.None,
            }));
            var rules = await _s.BrowseAsync(null, null, 0, browse, ct).ConfigureAwait(false);
            var dataTypes = await DataTypesAsync(children.Where(c => c.Class == NodeClass.Variable).Select(c => c.Id).ToList());
            var result = new List<Declaration>();
            for (var i = 0; i < children.Count; i++)
            {
                var r = rules.Results[i];
                if (!StatusCode.IsGood(r.StatusCode) || r.References.Count == 0) continue;
                var rule = ExpandedNodeId.ToNodeId(r.References[0].NodeId, _s.NamespaceUris);
                var c = children[i];
                result.Add(new Declaration(c.Id, c.Name, c.Class, c.TypeDefinition, dataTypes.GetValueOrDefault(c.Id), rule, owner));
            }
            return result;
        }

        private async Task<IReadOnlyList<Child>> ChildrenAsync(NodeId node)
        {
            var browse = new BrowseDescription
            {
                NodeId = node, BrowseDirection = BrowseDirection.Forward, ReferenceTypeId = ReferenceTypeIds.HierarchicalReferences,
                IncludeSubtypes = true, NodeClassMask = (uint)(NodeClass.Object | NodeClass.Variable | NodeClass.Method),
                ResultMask = (uint)BrowseResultMask.All,
            };
            var children = new List<Child>();
            var response = await _s.BrowseAsync(null, null, 0, new BrowseDescriptionCollection { browse }, ct).ConfigureAwait(false);
            var result = response.Results[0];
            if (StatusCode.IsBad(result.StatusCode)) return children;
            Take(result.References);
            var continuation = result.ContinuationPoint;
            while (continuation is { Length: > 0 } && children.Count <= UaClient.MaxReferencesPerNode)
            {
                var next = await _s.BrowseNextAsync(null, false, new ByteStringCollection { continuation }, ct).ConfigureAwait(false);
                Take(next.Results[0].References);
                continuation = next.Results[0].ContinuationPoint;
            }
            return children;

            void Take(ReferenceDescriptionCollection refs)
            {
                foreach (var r in refs)
                {
                    if (r.NodeId.ServerIndex != 0 || ExpandedNodeId.ToNodeId(r.NodeId, _s.NamespaceUris) is not { } id) continue;
                    var typeDefinition = r.TypeDefinition != null && !NodeId.IsNull(r.TypeDefinition)
                        ? ExpandedNodeId.ToNodeId(r.TypeDefinition, _s.NamespaceUris) : null;
                    if (children.Any(c => c.Id == id)) continue;
                    children.Add(new Child(id, r.BrowseName, r.NodeClass, typeDefinition));
                }
            }
        }

        private async Task<Child> DescribeAsync(NodeId id)
        {
            var read = new ReadValueIdCollection
            {
                new ReadValueId { NodeId = id, AttributeId = Attributes.BrowseName },
                new ReadValueId { NodeId = id, AttributeId = Attributes.NodeClass },
            };
            var r = (await _s.ReadAsync(null, 0, TimestampsToReturn.Neither, read, ct).ConfigureAwait(false)).Results;
            var typeRefs = await _s.BrowseAsync(null, null, 0, new BrowseDescriptionCollection
            {
                new BrowseDescription
                {
                    NodeId = id, BrowseDirection = BrowseDirection.Forward, ReferenceTypeId = ReferenceTypeIds.HasTypeDefinition,
                    IncludeSubtypes = false, ResultMask = (uint)BrowseResultMask.None,
                },
            }, ct).ConfigureAwait(false);
            var type = typeRefs.Results[0].References.Count > 0 ? ExpandedNodeId.ToNodeId(typeRefs.Results[0].References[0].NodeId, _s.NamespaceUris) : null;
            var name = r[0].Value as QualifiedName ?? new QualifiedName(id.ToString());
            var nodeClass = r[1].Value is int c ? (NodeClass)c : NodeClass.Object;
            return new Child(id, name, nodeClass, type);
        }

        private async Task<Dictionary<NodeId, NodeId?>> DataTypesAsync(IReadOnlyList<NodeId> variables)
        {
            var types = new Dictionary<NodeId, NodeId?>();
            if (variables.Count == 0) return types;
            var read = new ReadValueIdCollection(variables.Select(v => new ReadValueId { NodeId = v, AttributeId = Attributes.DataType }));
            var r = (await _s.ReadAsync(null, 0, TimestampsToReturn.Neither, read, ct).ConfigureAwait(false)).Results;
            for (var i = 0; i < variables.Count; i++) types[variables[i]] = StatusCode.IsGood(r[i].StatusCode) ? r[i].Value as NodeId : null;
            return types;
        }

        private async Task<string> NameOfAsync(NodeId id)
        {
            if (_names.TryGetValue(id, out var known)) return known;
            var r = (await _s.ReadAsync(null, 0, TimestampsToReturn.Neither,
                new ReadValueIdCollection { new ReadValueId { NodeId = id, AttributeId = Attributes.BrowseName } }, ct).ConfigureAwait(false)).Results[0];
            return _names[id] = r.Value is QualifiedName q ? q.Name : id.ToString();
        }

        private async Task<bool> IsAbstractAsync(NodeId type)
        {
            if (_abstract.TryGetValue(type, out var known)) return known;
            var r = (await _s.ReadAsync(null, 0, TimestampsToReturn.Neither,
                new ReadValueIdCollection { new ReadValueId { NodeId = type, AttributeId = Attributes.IsAbstract } }, ct).ConfigureAwait(false)).Results[0];
            return _abstract[type] = StatusCode.IsGood(r.StatusCode) && r.Value is true;
        }

        private async Task<NodeId?> SuperOfAsync(NodeId type)
        {
            if (_super.TryGetValue(type, out var known)) return known;
            NodeId? super = null;
            try { super = await _s.TypeTree.FindSuperTypeAsync(type, ct).ConfigureAwait(false); }
            catch (ServiceResultException) { /* a type the server does not describe: no supertypes */ }
            return _super[type] = super == null || NodeId.IsNull(super) ? null : super;
        }

        private async Task<bool> IsSubtypeAsync(NodeId sub, NodeId super)
        {
            var seen = new HashSet<NodeId>();
            for (NodeId? t = sub; t != null && seen.Add(t); t = await SuperOfAsync(t))
                if (t == super) return true;
            return false;
        }
    }
}
