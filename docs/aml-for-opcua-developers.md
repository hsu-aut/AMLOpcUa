# AutomationML for OPC UA developers

This page is for readers who know OPC UA (NodeSets, ModellingRules, NodeIds,
clients such as UaExpert, modelers such as UaModeler) but not AutomationML.
It explains the AutomationML terms the plugin and its documents use, each with
its nearest OPC UA concept, and says in OPC UA terms what an export writes and
what the document server exposes. The [manual](manual.md) says what to do step
by step. The other way round, for AutomationML users:
[OPC UA for AutomationML users](opcua-for-aml-users.md).

## AutomationML and CAEX

AutomationML (IEC 62714) is an XML format for plant engineering data. Its
core is CAEX (IEC 62424): a tree of the plant's objects with their classes,
roles, interfaces and attributes. Geometry and behaviour live in other formats
that the document references; the plugin works on the CAEX part only.

- An **.aml** file is one CAEX XML document.
- An **.amlx** file is a container (Open Packaging Conventions, a zip) that
  holds a root .aml document and further parts such as geometry. The plugin
  and `uaaml` read and write the root document and keep the other parts.
- Two CAEX versions are in use, **2.15** and **3.0** (AutomationML 2.10). The
  OPC UA libraries need CAEX 3.0; the plugin writes a CAEX 3.0 copy of a 2.15
  document ([CAEX 2.15 documents](manual.md#caex-215-documents)). The export
  reads both.

AutomationML e.V. distributes the **AutomationML Editor** for Windows, with
its licence options, at
<https://www.automationml.org/about-automationml/aml-tools/automationml-editor/>.
The plugin needs version 6.4 or later and installs through the editor's PlugIn
Manager ([Installing a release](manual.md#installing-a-release)).

## CAEX terms and their nearest OPC UA concept

| CAEX term | What it is | Nearest in OPC UA |
|---|---|---|
| InstanceHierarchy | A top-level tree of one plant's objects | The instances below Objects |
| InternalElement | An object in that tree, nested in its parent | An Object, or a Variable (see below) |
| SystemUnitClass | A reusable class of elements, with the child elements every element of it starts with | An ObjectType or VariableType with its instance declarations |
| `RefBaseSystemUnitPath` | The class an InternalElement was made from | HasTypeDefinition |
| `RefBaseClassPath` | The base class of a class | HasSubtype, seen from the subtype |
| RoleClass | An abstract meaning an element can have (Resource, Product, …) | Loosely an InterfaceType; Annex A maps InterfaceTypes to RoleClasses |
| SupportedRoleClass | A role an element or class plays | HasInterface |
| InterfaceClass | The class of a connection point | A ReferenceType |
| ExternalInterface | A connection point on an element or class | One end of a reference |
| InternalLink | Joins two ExternalInterfaces | The reference itself |
| Attribute | A named value with a data type, possibly a unit and nested attributes | A Variable or Property, but see below |
| AttributeType | A reusable definition of an attribute | A DataType |
| ID | A GUID per element and class | Loosely a NodeId; Annex A writes the NodeId into the IDs of what it imports |

The plugin imports a NodeSet by OPC 10000-83 Annex A. Each namespace becomes
up to four libraries named after its URI, one per kind of node it defines: `ATL_<uri>` (attribute types, from the
DataTypes), `ICL_<uri>` (interface classes, from the ReferenceTypes),
`RCL_<uri>` (role classes, from the InterfaceTypes) and `SUC_<uri>` (system
unit classes, from the ObjectTypes, VariableTypes, Methods and
InterfaceTypes).
Libraries ending in `OpcAmlMetaModel` hold the classes of Annex A itself. An
import brings types only; objects that the NodeSet itself holds come in
through the modeler's **Apply to document**
([import](import.md), [Apply the model](manual.md#apply-the-model-to-the-document)).

Annex A uses attributes for the attributes of a node (`NodeId`,
`BrowseName`, `ValueRank`, `Value`), not for its children. **A Variable or
Property is an InternalElement of its own**, of a VariableType class such as
`PropertyType` or `BaseDataVariableType`, with its value in the attribute
`Value`. A reference between a parent and a child is the nesting of elements,
and in the type libraries also an ExternalInterface on each side (such as
`HasComponent` and `ComponentOf`) joined by an InternalLink; the ModellingRule
is an attribute of the child's interface. AutomationML models made without
OPC UA usually put values into plain attributes of an element. The plugin
neither reads such attributes from a server nor serves them.

## What you get from an export

**Export NodeSet…** and `uaaml export` write a NodeSet in one of two modes
([Exporting as a NodeSet](manual.md#exporting-as-a-nodeset)).

### The document, by the AML-UA-XSLT rules (default)

These are the rules of the joint working group of AutomationML e.V. and the
OPC Foundation, the successor of OPC 30040. They describe the AutomationML
document in OPC UA, not the plant in its own OPC UA model. In OPC UA terms:

- **Namespaces.** The file gets `http://opcfoundation.org/UA/AML/<FileName>`,
  after the document's `FileName` attribute, which normally is the file's
  name. Every InstanceHierarchy and every library gets
  `http://opcfoundation.org/UA/AML/<its name>`. Renaming a file or an
  InstanceHierarchy therefore gives its nodes another namespace.
- **NodeIds** are strings: `ns=n;s={<ID>}` for an InternalElement,
  `<owner>_<name>` for an attribute.
- **Objects.** Every InternalElement is an Object, held by HasComponent from
  its parent, also one that Annex A made from a UA Variable. Annex A's property
  `Manufacturer` of a DI instance comes out as an Object whose type is an
  ObjectType `PropertyType`, with a Variable `Value` below it.
- **Type definitions.** An InternalElement gets HasTypeDefinition to the
  ObjectType made from its SystemUnitClass, `CAEXObjectType` when it has no
  class, and **none** when its class is in no library of the document and not
  reached through an `ExternalReference`. OPC UA requires a type definition
  for every Object.
- **Imported OPC UA models come out as AML classes.** An instance of DI's
  `SoftwareType` is typed by an ObjectType `SoftwareType` in the namespace
  `http://opcfoundation.org/UA/AML/SUC_http://opcfoundation.org/UA/DI/`, not
  by DI's own type. Every library of the document is exported: a document
  with the UA base model, DI and one instance gave about 220,000 nodes in
  240 MB.
- **Library and class BrowseNames** carry no namespace index, so they are in
  namespace 0.
- **Variables.** Every attribute is a Variable of `AMLBaseVariableType` or of
  the VariableType made from its AttributeType. The DataType follows a table
  ([export](export.md#mapping)): `xs:date` gives DateTime, `xs:anyURI` gives
  QualifiedName, and a type missing from the table, such as `xs:dateTime`,
  gives **no DataType**. Values of DateTime, Guid, Duration, Decimal and the
  like are written as String.
- **Required models.** The file requires the UA base model 1.04.3 and the AML
  base types 1.0.1 of 2019-09-09; the working group's base types NodeSet
  (`Opc.Ua.AMLBaseTypes.NodeSet2.xml`), which every loader needs, declares
  1.00 of 2016-02-22; the OPC Foundation's .NET stack loads the pair
  nonetheless. A document that uses the AutomationML standard
  libraries without holding them, as a document of the AutomationML Editor
  does through an `ExternalReference`, also needs
  `Opc.Ua.AMLStandardLibraries.NodeSet2.xml`. Both ship in the folder
  `nodesets` of the plugin and of the repository.

The tests check every output against `UANodeSet.xsd` and load it with the
OPC Foundation's .NET stack. Where the output departs from the rules, and why:
[export](export.md#deviations-from-aml-ua-xslt). What survives the way out and
back: [round trip](roundtrip.md).

### An imported model, as it was (`--annex-a-inverse`)

This mode writes back one OPC UA model that an import put into the document:
its ObjectTypes, VariableTypes, DataTypes, ReferenceTypes, methods and
objects, with their NodeIds, so the result can replace the original NodeSet.
It is not a standard; it is the inverse of Annex A. Documentation, type
dictionaries and what else Annex A does not carry do not come back
([export](export.md#second-mode-the-inverse-of-annex-a)). Instances created
with **New instance…** carry no NodeId and are not written.

## What the document server exposes

**Serve** on the **Server** tab, or `uaaml serve <doc.aml>`, starts an OPC UA
server whose address space is the document's instance hierarchies
([Serving the document](manual.md#serving-the-document-as-an-opc-ua-server)):

- **Endpoint** `opc.tcp://127.0.0.1:48400/AMLOpcUa` for this computer only;
  with **to the network** (`--network`) `opc.tcp://<computer name>:48400/AMLOpcUa`.
  ApplicationUri `urn:AMLOpcUa:DocumentServer`.
- **Below Objects**, one folder (FolderType, Organizes) per InstanceHierarchy.
- **Each InternalElement** is a node held by HasComponent, properties
  included. It is a Variable of `BaseDataVariableType` when its class is a UA
  VariableType; when its class comes from no OPC UA library, it is one if it
  has a `Value` attribute and no children. Otherwise it is an Object of
  **`BaseObjectType`**, whatever its class: a DI instance shows its children,
  but not its type. The server holds no types of the document.
- **NodeIds.** An element with an Annex A NodeId keeps it, unless it lies in
  the UA base namespace; the others get a string NodeId from their path in
  the namespace `urn:amlopcua:document`. BrowseNames are the element names.
- **Values** come from the attribute `Value` only, scalar and typed by its
  AttributeDataType: `xs:boolean`, `xs:dateTime`, `xs:double`, `xs:decimal`
  (as Double), `xs:float`, `xs:int`, `xs:long`, `xs:integer` (as Int64),
  `xs:short`, `xs:unsignedInt`, `xs:unsignedShort` and `xs:unsignedByte` give
  their OPC UA types; every other type, and every value that does not parse,
  is served as String. Other attributes are not served.
- **Read only.** Only the current value can be read; nothing is writable and
  no method is offered.
- **Structure at start.** Values follow the document while it is served;
  added or removed elements need a restart.
- **Simulate values** lets numbers swing around the document's value and
  booleans toggle, as in a running plant; the document keeps its values.
- **Security.** For this computer only: an unsecured endpoint besides the
  secured ones, every client admitted. To the network: secured endpoints only
  (Basic256Sha256 and newer, Sign and SignAndEncrypt), and only clients whose
  certificate was trusted under **Clients…** or with `uaaml clients --trust`.

To try a client against a model **with its types** (typed objects, state
machines, DataTypes), serve the NodeSet instead: `uaaml serve --nodeset
<file|model uri>` loads the models it requires, listens on port 4840 and
offers each model as the NamespaceFile of its namespace metadata
([servers](server.md)). In the plugin this server runs the practice plant
([A plant to practise on](manual.md#a-plant-to-practise-on)).

## How it relates to UaExpert and UaModeler

Neither is replaced. The plugin covers the part of each that an AutomationML
document needs.

**UaExpert**, the general OPC UA client. The plugin's client connects with
security and certificate trust, browses Objects and Views, reads values,
follows them by subscription, and fetches a server's NodeSet or rebuilds it by
browsing. It does not write values, call methods, show events, alarms or
history, or discover servers on the network. For diagnosing a server
UaExpert stays the tool; the plugin's client exists to bring a server into an
AutomationML document and keep it bound there.

**UaModeler**, the information model editor. The **Modeler** tab edits
NodeSets graphically: types, instance declarations with ModellingRules,
DataTypes, method arguments, values, references, instances and checks. It
saves a NodeSet or applies it to the document. It generates **no code** of
its own. For C# code, write the model with **ModelDesign…** (or `uaaml design
export`) and run the OPC Foundation's ModelCompiler on the design
([ModelDesign](modeldesign.md)). `uaaml design compile <design.xml> -o
<folder>` does that run and lists every file the compiler wrote. With
ModelCompiler 2.8.15 these were, for a model named Mini, besides
`Mini.NodeSet2.xml`: the C# sources `Mini.Classes.cs`, `Mini.Constants.cs`,
`Mini.DataTypes.cs` and `Mini.PredefinedNodes.cs` for the OPC Foundation's
.NET stack, `Mini.NodeIds.csv`, `Mini.Types.xsd` and `Mini.Types.bsd`, and
constants for C#, JavaScript, Python and TypeScript under `Constants`.
`uaaml` itself uses only the NodeSet; building a server from the C# sources
is the stack's business and was not tried here.

What neither tool does and the plugin does: OPC UA models as AutomationML
libraries, instances of UA types in an AutomationML document and their check
against the types, the document as a NodeSet, the document as a server, and
a running server's nodes and values in the plant model.

## The command line

`uaaml` does most of what the plugin does, for scripts and tests. It comes
with the plugin, as `uaaml.exe` in the plugin's folder `converter`, and runs
on .NET 8 or newer. `uaaml help` lists every command; the exit codes are in
the [README](../README.md#command-line) and in
[The command line tool uaaml](manual.md#the-command-line-tool-uaaml).
