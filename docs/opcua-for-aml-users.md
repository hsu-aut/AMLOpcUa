# OPC UA for AutomationML users

This page explains the OPC UA terms the plugin uses, for readers who know
AutomationML and the AutomationML Editor but have not worked with OPC UA. For
each term it says what becomes of it in the document. The
[manual](manual.md) says what to do step by step; the "?" buttons of the
plugin explain the same terms in a sentence each. The other way round, for
OPC UA developers: [AutomationML for OPC UA developers](aml-for-opcua-developers.md).

## Servers, nodes and the address space

An OPC UA server publishes its data as **nodes**. Every node has a node class,
an address (its NodeId), a name (its BrowseName) and references to other
nodes. All nodes a server offers form its **address space**. A client starts
at the folder **Objects** and follows references from there; that is what
"browsing" means.

The plugin reads OPC UA models by OPC 10000-83 Annex A, the mapping of OPC UA
to AutomationML in the OPC Foundation's specification for offline
engineering (UAFX). What each node class becomes:

| Node class | What it is | In the document (Annex A) |
|---|---|---|
| Object | A thing: a device, a module, a folder | An InternalElement |
| Variable | A value that can change, with a data type | An InternalElement of its own, of a VariableType class such as `BaseDataVariableType`; the value is in its attribute `Value` |
| Property | A Variable of `PropertyType` that describes its parent (a serial number, a unit, a range) | The same: an InternalElement of class `PropertyType` with an attribute `Value` |
| Method | A function a client can call, with input and output arguments | An InternalElement of class `UaMethodNodeClass` with the children `InputArguments` and `OutputArguments`. The plugin does not call methods. |
| ObjectType | The type of Objects; it declares the children each instance gets | A SystemUnitClass in the library `SUC_<namespace URI>` |
| VariableType | The type of Variables | A SystemUnitClass in the same library |
| DataType | The type of a value: Int32, String, an enumeration, a structure | An AttributeType in the library `ATL_<namespace URI>` |
| ReferenceType | The kind of a link between nodes: HasComponent, HasProperty, Organizes | An InterfaceClass in the library `ICL_<namespace URI>`; one that is not symmetric gives a pair, such as `HasComponent` and `ComponentOf` |
| InterfaceType | A set of children a type promises to have | A RoleClass in `RCL_<namespace URI>` and a SystemUnitClass; a type that implements it lists the RoleClass as SupportedRoleClass |

### A value is an element, not an attribute

In AutomationML a value is usually an attribute of an element. In OPC UA a
value is a node of its own. The plugin follows Annex A, so **every OPC UA
variable, properties included, is a child InternalElement of the element it
belongs to, and its value sits in the attribute `Value` of that child.** A
motor with a speed is an element `Motor1` with a child element `Speed` that
carries `Value`, not an element `Motor1` with an attribute `Speed`.

A child element bound to a server looks like this in the CAEX file:

```xml
<InternalElement Name="Motor1" ID="…">
  <InternalElement Name="Speed" ID="…">
    <Attribute Name="NodeId" RefAttributeType="[ATL_http://opcfoundation.org/UA/]/[NodeId]">
      <Attribute Name="ServerInstanceUri" AttributeDataType="xs:anyURI"><Value>urn:plc-17:Server</Value></Attribute>
      <Attribute Name="RootNodeId" RefAttributeType="ATL_OpcAmlMetaModel/ExplicitNodeId">
        <Attribute Name="NamespaceUri" AttributeDataType="xs:anyURI"><Value>http://example.com/Line1/</Value></Attribute>
        <Attribute Name="NumericId" AttributeDataType="xs:long"><Value>6021</Value></Attribute>
      </Attribute>
    </Attribute>
    <Attribute Name="Value" AttributeDataType="xs:double"><Value>1450</Value></Attribute>
  </InternalElement>
</InternalElement>
```

This decides what the plugin can do with your model:

- **Read values into document** and **Keep document values live** write into
  the attribute `Value` of every element that has both a `NodeId` and a
  `Value` attribute. They
  also read two older ways of binding an attribute: the DataVariable of
  AutomationML BPR 007 (an attribute with the sub-attributes `NodeId` and
  `RefDataSource`) and the sub-attribute `aml-opcua-variable` of earlier
  research work. The plugin reads both but cannot create them.
- **Bind to element…** offers InternalElements only, never attributes.
- When the plugin serves the document as a server, it serves elements; of
  their attributes only `Value` becomes a value.

An attribute `Speed` of `Motor1` is therefore neither read nor served. Model it
as a child element `Speed` with a `Value` attribute, then bind that element.

## References and the Objects folder

A **reference** is a typed, directed link between two nodes. The hierarchical
ones (HasComponent, HasProperty, Organizes) build the tree you browse. Others
link across it: HasTypeDefinition from an instance to its type, HasSubtype
from a type to its subtypes.

In the document the tree of hierarchical references is the nesting of
elements. HasTypeDefinition is the class an element was made from
(`RefBaseSystemUnitPath`), HasSubtype the base class of a class
(`RefBaseClassPath`). In the type libraries Annex A also writes each reference
as an ExternalInterface on both sides, for instance `HasComponent` on the
parent and `ComponentOf` on the child, joined by an InternalLink. **Check**
reports links that point nowhere (UA005) or join interfaces Annex A does not
pair (UA006).

**Objects** is the folder where the part of a server meant for browsing
starts; Views, where a server has any, are further starting points. When you
[take nodes into the document](manual.md#take-nodes-into-the-document), the
nodes on the way from Objects come along, so each element keeps its path.
When the plugin [serves the document](manual.md#serving-the-document-as-an-opc-ua-server),
each InstanceHierarchy becomes a folder below Objects.

## NodeId, namespace and BrowseName

A **namespace** is a URI that owns a set of nodes, such as
`http://opcfoundation.org/UA/DI/` for Device Integration. Every model has
one; the UA base model is `http://opcfoundation.org/UA/`. A server numbers its
namespaces, and a client sees a node as, say, `ns=3;i=6021`. That number
differs from server to server, so a document never stores it: Annex A keeps
the URI.

A **NodeId** is a node's address: its namespace and an identifier, which is
a number, a string, a GUID or a byte string. In the document it is the
attribute `NodeId` shown above, with `RootNodeId`, its `NamespaceUri` and one
of `NumericId`, `StringId`, `GuidId` or `OpaqueId`. A NodeId can also name its
node indirectly, by a path of BrowseNames or by an alias; the connected server
resolves such a NodeId when values are read
([servers](server.md#addressing-a-node-from-aml)). An instance created with
**New instance…** has no NodeId: a NodeId names a node of one server, and only
**Take into document** and **Bind to element…** give one.

A **BrowseName** is a node's name together with its namespace, such as
`Manufacturer` of `http://opcfoundation.org/UA/DI/`. The element's name is the
name part; the attribute `BrowseName` holds the namespace URI.

## Where a server's address is kept

Four names occur, and they mean different things:

| Name | What it holds | Where in the document |
|---|---|---|
| EndpointUrl | The URL a client connects to, `opc.tcp://host:port/path`, as entered in the connection box | Attribute of the element for the server that **Take into document** creates |
| ServerUri | The server's ApplicationUri | Attribute of the same element |
| ApplicationUri | The name an OPC UA program gives itself, unique and also written into its certificate. It often contains the computer's name (`urn:plc-17:…`). The plugin's document server calls itself `urn:AMLOpcUa:DocumentServer`. | No attribute of its own: ServerUri and ServerInstanceUri hold it |
| ServerInstanceUri | The server a NodeId belongs to, as its ApplicationUri | Sub-attribute of every NodeId that **Take into document** or **Bind to element…** writes. **Read values into document** and **Keep document values live** leave out elements whose ServerInstanceUri names another server. |

A server element described after AutomationML BPR 007 or DIN SPEC 16592 (an
element with the role OPCUA-Server) carries `EndpointURL` and `DiscoveryURL`
instead. **Copy without server addresses…** removes all of these attributes,
in every instance hierarchy of the copy
([Passing a document on](manual.md#passing-a-document-on)).

## Types, ModellingRules and placeholders

An ObjectType declares the children every instance of it gets, the
**instance declarations**. In the document they are the child elements of the
SystemUnitClass. A subtype keeps what its supertype declares.

Each instance declaration has a **ModellingRule**. Annex A writes it as the
attribute `ModellingRule` on the child's interface (`ComponentOf` or
`PropertyOf`):

| ModellingRule | In an instance |
|---|---|
| Mandatory | always there |
| Optional | there when you tick it in **New instance…** |
| MandatoryPlaceholder | the children you name for it, at least one |
| OptionalPlaceholder | the children you name for it, possibly none |
| ExposesItsArray | none; how many there are is known only where the values are |

A **placeholder** is a declaration whose name stands in angle brackets, such
as `<CPIdentifier>` in DI's `DeviceType`. It stands for any number of children
of its type. An instance never holds an element named in angle brackets; it
holds the children you named for it (**Check** reports such an element as
UA007). [Create an instance](manual.md#create-an-instance) and
[What an instance contains](manual.md#what-an-instance-contains) say how the
plugin does it.

## Companion specifications and abstract types

A **companion specification** is an information model for one domain,
published by the OPC Foundation together with a partner organisation, as a
NodeSet file with its own namespace. Most build on the UA base model and on
Device Integration (DI). The plugin brings DI along; the others come through
**Companion specs…**, which searches by the published name ("Device
Integration"), the short name ("DI") or the namespace, or through the UA
Cloud Library
([Companion specifications](manual.md#companion-specifications)).

An **abstract** type is meant only to be derived from: no server creates an
instance of it. In the document its class has the attribute `IsAbstract`
with the value `true`. **New instance…** hides abstract types unless **Show
abstract types** is ticked, and **Check** reports an instance of one as
UA002.

This matters for DI. Its `DeviceType` and `ComponentType` are abstract, and
the only concrete type DI declares below them is `SoftwareType`. A device
needs a concrete subtype from a model built on DI, for instance:

| Model | Concrete types |
|---|---|
| Robotics (`http://opcfoundation.org/UA/Robotics/`) | `MotionDeviceSystemType`, `MotionDeviceType`, `ControllerType`, `AxisType`, `MotorType` and others, all below `ComponentType` |
| The practice plant (`http://hsu-hh.de/UA/MPS500/`) | `StationType` below `DeviceType`; it comes in with **Types of the server…** in [the practice plant](manual.md#a-plant-to-practise-on) |

Or derive a type of your own from `DeviceType` in the
[Modeler](manual.md#the-modeler-tab) and apply it to the document.

## Reading values once, live, or only watching

A **subscription** asks a server to report changes instead of being asked
again and again: the server samples the values and sends what changed at a
fixed interval. The three value commands on the **Server** tab use it as
follows:

| Command | How it reads | Where the values go |
|---|---|---|
| **Read values into document** | One read of every bound value, no subscription | The `Value` attributes, once |
| **Keep document values live** | One subscription for every bound value; the server reports changes at most once a second | The `Value` attributes, collected and written together, until you stop, disconnect or close the document |
| **Watch (view only)** | One subscription for the variables you chose, twice a second | The list below the address space; nothing is written |

The document is not saved by any of them; Ctrl+S keeps the values read last
([Read, keep and watch values](manual.md#read-keep-and-watch-values)).

## Security in one paragraph

A server offers several **endpoints**: the same URL with different security.
The **security mode** says what is protected: None sends everything plainly,
Sign protects against tampering, SignAndEncrypt also keeps it secret. The
**security policy** names the algorithms, such as Basic256Sha256; newer ones
are stronger. With **Secure** ticked the plugin takes only signed endpoints,
the most secure the server offers. Both sides prove who they are with a
**certificate**. The first time, the plugin shows the server's certificate
with its SHA-256 fingerprint; compare it with the one the server's
administrator gives you before you trust it. The plugin keeps exactly that
certificate. Its own client certificate lies in
`%LOCALAPPDATA%\AMLOpcUa\pki\own\certs`, as `AMLOpcUa [<thumbprint>].der`; a
server that admits only known clients needs its administrator to trust that
file. A user name and password are sent only over a secured connection
([Connect](manual.md#connect)).

## How a value gets from a PLC into your model

There are two ways. Neither matches a node to an element of yours by its
name.

1. **Take into document** copies a part of the server into the document: below
   an element for the server, in the InstanceHierarchy named under **nodes
   into** (`OpcUaServer` unless you change it). Every element gets its NodeId,
   every variable a `Value`. Your own model does not change. Where an element
   of your model already carries the same NodeId, the copy refers to it with
   the attribute `refBaseObj`; an element of the same name is not matched.
2. **Bind to element…** gives one element of your model the NodeId of one
   node, and a `Value` attribute when the node is a variable. It binds one
   element at a time.

Then **Read values into document** or **Keep document values live** fill the
`Value` attributes of every bound element: the copies and your own elements
alike. Model each value you want from the PLC as a child element (see
[A value is an element, not an attribute](#a-value-is-an-element-not-an-attribute)).
Binding needs no types; **Types of the server…** only gives the copied
elements their classes.

The steps, with a plant to practise on: [Connect an existing plant model to a
PLC](manual.md#connect-an-existing-plant-model-to-a-plc) and
[A plant to practise on](manual.md#a-plant-to-practise-on).
