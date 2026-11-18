// Writing a value into a running server and calling a method of it, from text
// a user typed. The client looks up what the text has to become (the
// variable's data type and value rank, the method's argument list) and
// refuses a variable the server lets only be read; everything else the
// server answers itself.

using Opc.Ua;
using OpcUaAml.Addressing;

namespace OpcUaAml.Server;

/// <summary>What a variable holds and whether this client may write it.</summary>
public sealed record UaVariableInfo(string DataType, string BuiltInType, int ValueRank, bool Writable);

/// <summary>The server's answer to a write.</summary>
public sealed record UaWriteResult(bool Good, string Status);

/// <summary>An argument of a method (OPC 10000-3, Argument).</summary>
public sealed record UaArgument(string Name, string DataType, string BuiltInType, int ValueRank, string Description);

public sealed record UaMethodInfo(IReadOnlyList<UaArgument> Inputs, IReadOnlyList<UaArgument> Outputs, bool Executable);

/// <summary>The server's answer to a call, with the output arguments as text.</summary>
public sealed record UaCallResult(bool Good, string Status, IReadOnlyList<(string Name, string? Value)> Outputs);

public sealed partial class UaClient
{
    /// <summary>The data type (its name and built-in type), value rank and write access of a variable.</summary>
    public async Task<UaVariableInfo> DescribeVariableAsync(UaNodeAddress variable, CancellationToken ct = default)
    {
        var id = ToNodeId(variable);
        var read = new ReadValueIdCollection
        {
            new ReadValueId { NodeId = id, AttributeId = Attributes.DataType },
            new ReadValueId { NodeId = id, AttributeId = Attributes.ValueRank },
            new ReadValueId { NodeId = id, AttributeId = Attributes.AccessLevel },
            new ReadValueId { NodeId = id, AttributeId = Attributes.UserAccessLevel },
        };
        var r = (await _session.ReadAsync(null, 0, TimestampsToReturn.Neither, read, ct).ConfigureAwait(false)).Results;
        if (!StatusCode.IsGood(r[0].StatusCode) || r[0].Value is not NodeId dataType)
            throw new InvalidOperationException($"{variable} is not a variable: {Symbol(r[0].StatusCode)}.");
        var rank = r[1].Value is int vr ? vr : ValueRanks.Scalar;
        var access = r[2].Value is byte a ? a : (byte)0;
        var user = r[3].Value is byte u ? u : access;
        var builtIn = await TypeInfo.GetBuiltInTypeAsync(dataType, _session.TypeTree, ct).ConfigureAwait(false);
        var name = (await DataTypeNamesAsync([dataType], ct).ConfigureAwait(false))[0];
        return new UaVariableInfo(name, builtIn.ToString(), rank,
            (access & AccessLevels.CurrentWrite) != 0 && (user & AccessLevels.CurrentWrite) != 0);
    }

    /// <summary>
    /// Writes <paramref name="text"/> as the variable's value. A variable the
    /// server lets this client only read is not written (BadNotWritable); a
    /// text that is no value of its type throws ValueEntryException.
    /// </summary>
    public async Task<UaWriteResult> WriteValueAsync(UaNodeAddress variable, string text, CancellationToken ct = default)
    {
        var info = await DescribeVariableAsync(variable, ct).ConfigureAwait(false);
        if (!info.Writable) return new UaWriteResult(false, "BadNotWritable");
        var value = ValueEntry.ToVariant(info.BuiltInType, info.ValueRank, ValueEntry.Parse(info.BuiltInType, info.ValueRank, text));
        var write = new WriteValueCollection
        {
            new WriteValue { NodeId = ToNodeId(variable), AttributeId = Attributes.Value, Value = new DataValue(value) },
        };
        var response = await _session.WriteAsync(null, write, ct).ConfigureAwait(false);
        var status = response.Results[0];
        return new UaWriteResult(StatusCode.IsGood(status), Symbol(status));
    }

    /// <summary>The input and output arguments of a method (its properties InputArguments and OutputArguments) and whether it may be called.</summary>
    public async Task<UaMethodInfo> DescribeMethodAsync(UaNodeAddress method, CancellationToken ct = default)
    {
        var id = ToNodeId(method);
        var paths = new BrowsePathCollection();
        foreach (var name in new[] { BrowseNames.InputArguments, BrowseNames.OutputArguments })
            paths.Add(new BrowsePath
            {
                StartingNode = id,
                RelativePath = new RelativePath
                {
                    Elements = new RelativePathElementCollection
                    {
                        new RelativePathElement { ReferenceTypeId = ReferenceTypeIds.HasProperty, IsInverse = false, IncludeSubtypes = true, TargetName = new QualifiedName(name) },
                    },
                },
            });
        var found = await _session.TranslateBrowsePathsToNodeIdsAsync(null, paths, ct).ConfigureAwait(false);
        var read = new ReadValueIdCollection
        {
            new ReadValueId { NodeId = id, AttributeId = Attributes.Executable },
            new ReadValueId { NodeId = id, AttributeId = Attributes.UserExecutable },
        };
        var lists = new List<int>();
        for (var k = 0; k < found.Results.Count; k++)
        {
            if (!StatusCode.IsGood(found.Results[k].StatusCode) || found.Results[k].Targets.Count == 0) continue;
            read.Add(new ReadValueId { NodeId = ExpandedNodeId.ToNodeId(found.Results[k].Targets[0].TargetId, _session.NamespaceUris), AttributeId = Attributes.Value });
            lists.Add(k);
        }
        var values = (await _session.ReadAsync(null, 0, TimestampsToReturn.Neither, read, ct).ConfigureAwait(false)).Results;
        var executable = values[0].Value is not false && values[1].Value is not false;

        var arguments = new List<Argument>[] { [], [] };
        for (var j = 0; j < lists.Count; j++)
            if (values[2 + j].Value is ExtensionObject[] list)
                arguments[lists[j]].AddRange(list.Select(e => e.Body).OfType<Argument>());
        var types = arguments.SelectMany(a => a).Select(a => a.DataType).Distinct().ToList();
        var names = await DataTypeNamesAsync(types, ct).ConfigureAwait(false);
        var builtIns = new Dictionary<NodeId, string>();
        for (var t = 0; t < types.Count; t++)
            builtIns[types[t]] = (await TypeInfo.GetBuiltInTypeAsync(types[t], _session.TypeTree, ct).ConfigureAwait(false)).ToString();
        UaArgument Of(Argument a) => new(a.Name ?? "", names[types.IndexOf(a.DataType)], builtIns[a.DataType], a.ValueRank, a.Description?.Text ?? "");
        return new UaMethodInfo(arguments[0].Select(Of).ToList(), arguments[1].Select(Of).ToList(), executable);
    }

    /// <summary>
    /// Calls <paramref name="method"/> on <paramref name="target"/> with the
    /// input arguments as typed. A text that is no value of its argument's type
    /// throws ValueEntryException naming the argument; the server's status
    /// names a bad argument too.
    /// </summary>
    public async Task<UaCallResult> CallAsync(UaNodeAddress target, UaNodeAddress method, IReadOnlyList<string> inputs, CancellationToken ct = default)
    {
        var info = await DescribeMethodAsync(method, ct).ConfigureAwait(false);
        if (inputs.Count != info.Inputs.Count)
            throw new ArgumentException($"{method} takes {info.Inputs.Count} input argument(s), not {inputs.Count}.", nameof(inputs));
        var arguments = new VariantCollection();
        for (var i = 0; i < inputs.Count; i++)
        {
            try
            {
                var input = info.Inputs[i];
                arguments.Add(ValueEntry.ToVariant(input.BuiltInType, input.ValueRank, ValueEntry.Parse(input.BuiltInType, input.ValueRank, inputs[i])));
            }
            catch (ValueEntryException ex) { throw ex.For(info.Inputs[i].Name); }
        }
        var request = new CallMethodRequestCollection
        {
            new CallMethodRequest { ObjectId = ToNodeId(target), MethodId = ToNodeId(method), InputArguments = arguments },
        };
        var result = (await _session.CallAsync(null, request, ct).ConfigureAwait(false)).Results[0];
        var status = Symbol(result.StatusCode);
        for (var k = 0; k < result.InputArgumentResults.Count && k < info.Inputs.Count; k++)
            if (StatusCode.IsBad(result.InputArgumentResults[k]))
                status += $"; {info.Inputs[k].Name}: {Symbol(result.InputArgumentResults[k])}";
        var outputs = result.OutputArguments
            .Select((v, k) => (k < info.Outputs.Count ? info.Outputs[k].Name : $"#{k + 1}", ValueFormatting.ToAml(Portable(v.Value, _session.NamespaceUris))))
            .ToList();
        return new UaCallResult(StatusCode.IsGood(result.StatusCode), status, outputs);
    }

    /// <summary>The names of data types (their BrowseNames), in the order given.</summary>
    private async Task<IReadOnlyList<string>> DataTypeNamesAsync(IReadOnlyList<NodeId> types, CancellationToken ct)
    {
        if (types.Count == 0) return [];
        var read = new ReadValueIdCollection(types.Select(t => new ReadValueId { NodeId = t, AttributeId = Attributes.BrowseName }));
        var r = (await _session.ReadAsync(null, 0, TimestampsToReturn.Neither, read, ct).ConfigureAwait(false)).Results;
        return r.Select((v, k) => v.Value is QualifiedName q ? q.Name : types[k].ToString()).ToList();
    }

    private static string Symbol(StatusCode status) => StatusCode.LookupSymbolicId(status.Code) ?? status.ToString();
}
