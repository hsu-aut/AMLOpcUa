# Running servers

## Addressing a node from AML

Three conventions point from an AML document to a node of a running server.
`OpcUaAml.Addressing` reads and writes all three and converts between them
through one record, `UaNodeAddress` (namespace URI, identifier type,
identifier, optional server URI). Namespace indexes are never stored: they are
only valid for one session.

| Convention | Where | Form |
|---|---|---|
| OPC 10000-83 Annex A | attribute `NodeId` of an element | `RootNodeId` (`NamespaceUri` and one of `NumericId`, `StringId`, `GuidId`, `OpaqueId`), optionally `ServerInstanceUri` |
| MTP, VDI/VDE/NAMUR 2658 | ExternalInterface of class `OPCUAItem` | `Identifier` (its AttributeDataType gives the identifier type: `xs:string`, `xs:ID` for GUID, `xs:base64Binary`, `xs:int`), `Namespace` (URI), `Access` |
| AutomationML BPR 007 DataVariable | attribute with RefSemantic `aml-dataSourceType:OPCUA` | `NodeId` as `ns=<index>;…` and `RefDataSource` (ID of the element with role OPCUA-Server, whose `NameSpaceTable` resolves the index) |

The server element of a DataVariable may also carry the descriptions of DIN
SPEC 16592 clause 6: `SecurityPolicy`, `MessageSecurityMode`,
`TransportProfileURI` and `UserToken` are read into `DataSource`, and
`ToConnectOptions()` asks for a secured endpoint unless the mode or the policy
say None. Credentials are never taken from a document.

The older binding of Kühnert, Schleipen et al. (2016), a sub-attribute
`aml-opcua-variable` with `ServerAddress` and `VariableNodeId`, is read by
`LegacyOpcUaVariable` and filled by a snapshot; its namespace index belongs to
that server and is resolved through the connected server's table. It is never
written.

Annex A NodeIds that address a node indirectly are resolved by the connected
server when values are read or kept live: a `RootNodeId` with a `BrowsePath`
through TranslateBrowsePathsToNodeIds (a missing reference type means
HierarchicalReferences), an `Alias` through the `FindAlias` method of the
server's `Aliases` object (OPC 10000-17). A path or alias the server does not
know, or one that names several nodes, is reported for that element.

Checked against real files: all 268 OPCUAItems of the ZVEI stirred reactor MTP
(Siemens TIA), all 174 of the WAGO MTP from p2o-lab/MTPPy, and the DataVariable
example of the AutomationML e.V. (these files are not part of the repository).

The export turns a DataVariable into an `AMLOpcUaConnectionType` variable of
the AML base types, so the exported NodeSet still names the node and the
server it was bound to ([export](export.md), D16).

## Client

`UaClient` wraps the OPC Foundation .NET Standard stack:

- **Connect** to an endpoint, preferring a secured one; anonymous or user name
  and password. The client creates its own certificate under
  `%LOCALAPPDATA%\AMLOpcUa\pki`. An unknown server certificate is refused
  unless the caller accepts it; the plugin asks the user.
- **Browse** along hierarchical references (Objects folder by default).
- **Read** values, several in one request; a node that does not exist is a bad
  result, not an exception.
- **Watch** values through a subscription (`WatchAsync`); the plugin shows
  them in a live list under the address space. That list is for looking; to
  keep the document itself current, see "Keep values live" below.

## The server's types

A mirrored element gets its UA type as class only when the document holds the
library of that type. **Types of the server** (`ServerNodeSets`, on the command
line `uaaml nodeset`) takes the NodeSet of a namespace from the server itself,
so the libraries come from the same Annex A import as a NodeSet file:

1. If the server publishes the NodeSet as the `NamespaceFile` of the
   namespace's metadata (`Server/Namespaces`, OPC 10000-5 6.3.13), that file
   is read (FileType Open, Read, Close) and taken unchanged.
2. Otherwise the NodeSet is rebuilt by browsing: the namespace's types, found
   along HasSubtype from the roots of the four type hierarchies in every
   namespace, what they hold along hierarchical references (instance
   declarations of any depth) and their DataTypeEncodings. With "Include the
   namespace's objects" (`--instances`) also what the Objects folder leads to.
   The stack's NodeSet2 export writes attributes and references; values,
   DataType definitions (structures, unions, enumerations, OptionSets),
   ParentNodeId and MethodDeclarationId are read or derived here, because the
   export leaves them out. Each reference appears once where NodeSets usually
   put it (HasSubtype at the subtype, HasEncoding at the encoding).
3. A model the NodeSet requires that neither the NodeSet folders nor the
   bundled NodeSets provide is fetched from the server the same way.

The files go to `%LOCALAPPDATA%\AMLOpcUa\server-nodesets\<server>`. The
dialog offers to import the types into the document, to open the NodeSet in
the modeler, or to save it.

A rebuilt NodeSet lacks what a server does not expose: Documentation links,
Category, SymbolicName, the deprecated type dictionaries (OPC 10000-5 D) and
nodes that no reference leads to. DI, for instance, defines nine objects that
only name well-known FunctionalGroups and hang below nothing. Checked with DI
served from its NodeSet: apart from these, the rebuilt NodeSet equals the
original, and its import gives the same AML libraries as the import of the
file (`ServerNodeSetTests`).

## Into the document

- **Take into document** (`AddressSpaceMirror`): the checked parts (see
  above), or with nothing checked the selected node and the nodes below it (up
  to a depth, at most 2000 nodes by default, the "max" field or `--max-nodes`), become InternalElements.
  Each carries its Annex A NodeId including the server's ApplicationUri, and
  its UA type as RefBaseSystemUnitPath when the document holds the type's
  library (matched by the NodeId every generated class carries). Variables get
  a `Value` attribute with the current value and an XML Schema data type.
  When the document already models a node, that is an element with the same
  NodeId (the planned object), the mirrored element becomes an aspect of it:
  a `refBaseObj` of the AutomationML object reference attribute types (Drath,
  Nabizada, 1.1.1-beta) holds the planned element's ID. The planned model is
  not changed, the library is added to the document if missing. Planned
  elements are searched in a chosen InstanceHierarchy (`--plan` on the
  command line), or in the whole document outside the target, leaving out
  elements that are aspects themselves. A node claimed by several planned
  elements is not linked, and a type that differs between server and plan is
  reported; both appear as notes. `--no-link` turns linking off.
- **Bind to element**: writes the selected node's NodeId attribute onto an
  element of your choice; the editor then selects that element.
- **Read current values** (`ValueSnapshot`): every element with a NodeId and a
  `Value` attribute, and every BPR DataVariable, gets the current value.
  The parent attribute of an `aml-opcua-variable` binding gets its value too.
  Bindings to another server (a different ServerInstanceUri or data source
  endpoint) are skipped. On the command line, `uaaml snapshot <doc.aml>`
  without an endpoint connects to every server the document names as a data
  source, with the security its description asks for. Nothing is subscribed; the document records one
  point in time.
- **Keep values live** (`LiveValues`): the same bindings, subscribed. Every
  change the server reports is written into the document (on the editor's UI
  thread, one monitored item per node however many elements are bound to it)
  until the button is pressed again, the client disconnects or the document
  is closed. The document is not saved; Ctrl+S keeps the last values.

## Selecting what to mirror

A mirror follows Annex A, so every variable and property is an element of its
own; a DI device alone gives 50 to 100 elements. What goes into the document
is therefore chosen (`MirrorSelection`, `MirrorPlan`):

- **Check boxes** in the address tree mark parts; right click says how much:
  the node only, with its children, or with everything below it (down to the
  depth given, 0 for no limit but the node limit, 2000 unless set otherwise).
- **Filters** apply to the tree and to what is taken: leave out properties,
  objects only, show or hide the Server object (hidden by default),
  namespaces to take (nodes of other namespaces are left out with what they
  hold).
- **Instances of a type** below a node: the server's ObjectTypes and
  VariableTypes are listed, the instances found (with subtypes, not searched
  below a match) are shown with check boxes; unchecked ones are left out.
- **Views** of the server appear as a further root of the tree; a View is
  browsed with its own references.
- **Leave out** takes a node out of checked parts.
- **Count** gives the number of elements before anything is written.

Each part keeps the way to it: below an element for the server (with
`ServerUri` and `EndpointUrl`), the nodes from the Objects or Views folder
down to the part become elements with their NodeId, the part itself gets its
content. The selection is kept as attribute `MirrorSelection` of that server
element; **Load kept selection** takes it back into the tree.

When the hierarchy already holds a mirror of the server, the plugin asks:
update it (found by NodeId: types and values read again, new nodes added)
or mirror into a new hierarchy. Elements whose node the server no longer
holds are reported, and as chosen in that dialog kept, marked with the
attribute `NotOnServer` (the mark goes when the node is back) or removed. On the command line `uaaml mirror` updates (`--vanished
report|mark|remove`), `--copy` mirrors anew, `--preview` counts, and without nodes the kept selection is
mirrored again. Elements of a mirrored selection never count as planned
elements for the refBaseObj link, so a second copy does not link to the first.

## The document as a server

**Serve this document** (`AmlServerHost`) starts a local OPC UA server whose
address space is the document's instance hierarchies, so clients can be tested
against the engineering model before the plant exists. Each instance
hierarchy becomes a folder under Objects; elements become Objects, or
Variables if their UA type is a VariableType or they carry a `Value` and no
children. Elements with an Annex A NodeId keep it (the namespace is
registered), the others get a string NodeId from their path in
`urn:amlopcua:document`. Values come from the `Value` attributes, typed by
their AttributeDataType. The nodes are those of the document at start; their
values follow it: a change in the document reaches the served nodes after a
short quiet time, and subscribed clients see it (`FollowDocument`,
`RefreshValues`). Added or removed elements need a restart; the plugin says
so once. `AmlServerTests` serves a document and
mirrors it back: structure and values survive.

Values are written as AML holds them: invariant culture, XML Schema lexical
forms, arrays separated by spaces.

## Tests

`ServerNodeSetTests` serve DI from its NodeSet, once without and once with a
NamespaceFile, and compare what comes back with the file.
`ServerTests` and `MirrorTests` start an OPC UA server inside the test process
(`TestServer`, a small plant namespace on a free port, with a counter that
changes every 100 ms) and cover secured and unsecured sessions, the refused
unknown certificate, browsing, reading, subscriptions, mirroring with types
and values, limits, and snapshots including DataVariables of another server
and bindings to missing nodes.
