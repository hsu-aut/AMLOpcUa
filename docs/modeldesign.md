# ModelDesign and the OPC UA ModelCompiler

ModelDesign is the form in which the OPC Foundation writes its own information
models before they become NodeSets: one XML file per model
(`http://opcfoundation.org/UA/ModelDesign.xsd`) plus an identifier file (CSV)
that gives every node its NodeId. The
[ModelCompiler](https://github.com/OPCFoundation/UA-ModelCompiler) turns such a
design into a NodeSet and into code for the .NET stack.

AMLOpcUa works with NodeSets. The two directions here connect it to that world:

- **out**: a NodeSet of the document (or of the modeler) written as a design,
  so a model built here can be taken into a workflow that generates code;
- **in**: a design compiled into a NodeSet, and from there through Annex A
  into the document, so a model that exists only as a design can be used here.

Nothing of the compiler's code generation is used, and the compiler is not
shipped: it is an external tool, looked for where its installer puts it.

## Installing the compiler

```bash
dotnet tool install --global OPCFoundation.Opc.Ua.ModelCompiler.Tool
```

The command is then `Opc.Ua.ModelCompiler`. AMLOpcUa looks for it in
`%USERPROFILE%\.dotnet\tools`, on the PATH, and wherever the environment
variable `AMLOPCUA_MODELCOMPILER` or `--compiler` names it. Without it, the
export still works; only compiling needs it, and the message says how to get
it.

## Commands

```bash
uaaml design export Opc.Ua.Di.NodeSet2.xml -o DI.xml     # writes DI.xml and DI.csv
uaaml design compile DI.xml -o ./compiled                # ModelCompiler, reports the NodeSet
uaaml design import DI.xml --into plant.aml              # compile and import in one go
```

`export` writes two files: the design and, beside it with the same name, the
identifier file. `compile` and `import` use that file when it is there, so the
compiled model keeps the NodeIds of the original; without one the compiler
hands out its own.

## In the plugin

The namespace view has **ModelDesign…** beside **Documentation…**: it writes
the selected model as a design with its identifier file beside it. The model
comes out of the document itself (the inverse of Annex A), so the design holds
what the document holds now, including what was changed in the modeler.

The other way round needs no button: a ModelDesign chosen for import or
dropped on the plugin is recognised by its namespace, compiled, and the NodeSet
that comes out is imported as any other. Where the compiler is not installed,
the status line says so and how to get it. `ModelCompilerPath` in the settings
names a compiler of one's own.

## What the design holds

Of the model's own nodes: ObjectTypes, VariableTypes, DataTypes and
ReferenceTypes with their supertype, their instance declarations (with
ModellingRule, type definition, DataType and ValueRank, nested as deep as they
go), the arguments of methods, the fields and values of DataTypes, the inverse
name of a ReferenceType, and the references that are not the ones that hold a
child. Objects and variables of the model that no type declares are written as
they stand.

Names: a node's SymbolicName is its BrowseName, with the characters XML cannot
carry in a QName replaced, and with a number appended when a sibling had the
name first, because two nodes may well share a BrowseName. Whenever the name
could not be kept as it was, the BrowseName is written out as an element, as
placeholders such as `<CPIdentifier>` need. A BrowseName of the UA namespace
(`StateNumber`, `Id`) keeps that namespace as a prefix. Types are written in
the order of their derivation, because the compiler resolves a BaseType while
it reads the file.

A node that no parent declares (its own inverse reference is the only thing
that holds it, or two nodes hold each other) is written on its own rather than
dropped: a reference must not point at a node the file does not hold. A node
two parents hold belongs to one of them and is a reference in the other. A
NodeId that is a string goes onto the node as `StringId`; one that is a GUID
cannot be carried, and the model is then compiled with identifiers of the
compiler's own.

Left out, because the compiler makes them from the design itself: the
encodings of a structure, the names of an enumeration (EnumStrings,
EnumValues, OptionSetValues), the arguments as a property, the type
dictionaries and the namespace metadata. Their NodeIds are in the identifier
file under the names the compiler gives them
(`FetchResultDataType_Encoding_DefaultBinary`), so they keep them anyway.

## What a round trip keeps, measured

The bundled DI (1.05.0, 447 nodes of its own) written as a design and compiled
again with ModelCompiler 2.8.15: **417 of 447 nodes come back with the same
NodeId, node class and BrowseName**. The 30 others are the nodes the compiler
manages itself and numbers itself: the namespace metadata object with its
properties, the two type dictionaries with their descriptions, and the JSON
encodings, which this version of the compiler does not write.

A model of the size a companion specification starts at comes back whole. A
state machine written in the modeler (OPC 10000-5 Annex B: three states, three
transitions, their numbers, FromState, ToState and HasCause) goes out as a
design and comes back as **16 of 16 nodes identical**, references included.
The same model through AutomationML and back (Annex A in, its inverse out)
keeps every node too; what differs there are attributes at their default
(ValueRank -1, DataType BaseDataType) and the aliases of the file.

One loss is the compiler's own: the arguments of a method are in the design and
in the code it generates, but the NodeSet it writes carries InputArguments and
OutputArguments as a node without a value. A NodeSet that goes in as a NodeSet
(the compiler reads those too) keeps its argument values.

Not carried at all: the `Documentation` attribute of a node (the design has no
place for it), values of variables, and everything outside the model's own
namespace.
