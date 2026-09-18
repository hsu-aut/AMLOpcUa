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

Annex A NodeIds that address a node through an `Alias` or a `BrowsePath`
are reported as such; resolving them needs a server and is not done yet.

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
  them in a live list under the address space. Watched values are not written
  into the document.

## Into the document

- **Take into document** (`AddressSpaceMirror`): the selected node and the
  nodes below it (up to a depth, at most 2000 nodes) become InternalElements.
  Each carries its Annex A NodeId including the server's ApplicationUri, and
  its UA type as RefBaseSystemUnitPath when the document holds the type's
  library (matched by the NodeId every generated class carries). Variables get
  a `Value` attribute with the current value and an XML Schema data type.
- **Bind to element**: writes the selected node's NodeId attribute onto an
  element of your choice; the editor then selects that element.
- **Read current values** (`ValueSnapshot`): every element with a NodeId and a
  `Value` attribute, and every BPR DataVariable, gets the current value.
  Bindings to another server (a different ServerInstanceUri or data source
  endpoint) are skipped. Nothing is subscribed; the document records one
  point in time.

## The document as a server

**Serve this document** (`AmlServerHost`) starts a local OPC UA server whose
address space is the document's instance hierarchies, so clients can be tested
against the engineering model before the plant exists. Each instance
hierarchy becomes a folder under Objects; elements become Objects, or
Variables if their UA type is a VariableType or they carry a `Value` and no
children. Elements with an Annex A NodeId keep it (the namespace is
registered), the others get a string NodeId from their path in
`urn:amlopcua:document`. Values come from the `Value` attributes, typed by
their AttributeDataType. The address space is a snapshot of the document at
start; restart to pick up changes. `AmlServerTests` serves a document and
mirrors it back: structure and values survive.

Values are written as AML holds them: invariant culture, XML Schema lexical
forms, arrays separated by spaces.

## Tests

`ServerTests` and `MirrorTests` start an OPC UA server inside the test process
(`TestServer`, a small plant namespace on a free port, with a counter that
changes every 100 ms) and cover secured and unsecured sessions, the refused
unknown certificate, browsing, reading, subscriptions, mirroring with types
and values, limits, and snapshots including DataVariables of another server
and bindings to missing nodes.
