# Export: AML documents to OPC UA NodeSets

## Rules

The export follows the current rules of the joint working group of
AutomationML e.V. and the OPC Foundation. The working group publishes them as
XSLT 2.0 stylesheets in [AutomationML/AML-UA-XSLT](https://github.com/AutomationML/AML-UA-XSLT)
(MIT); this project ports commit `a144dcc` (2026-09-17) to C#, because .NET
has no XSLT 2.0 processor. The rules revise OPC 30040 "OPC UA for
AutomationML" v1.00 (2016); the differences are listed below.

`NodeSetExporter` (`dotnet/OpcUaAml.Core/Export/`) reads CAEX 2.15 and CAEX 3.0
documents, as a file, an `XDocument` or an Aml.Engine `CAEXDocument`, and
returns the NodeSet as an `XDocument`. `AmlUaXsltTranslator` holds the port:
one method per XSLT template, named after it.

```bash
uaaml export plant.aml -o plant.NodeSet2.xml
uaaml export plant.aml -o plant.NodeSet2.xml --date 2026-09-18 --xslt-compatible
```

```csharp
var nodeSet = NodeSetExporter.ExportFile("plant.aml", new NodeSetExportOptions
{
    PublicationDate = new DateTime(2026, 9, 18, 0, 0, 0, DateTimeKind.Utc),
});
```

Options:

- `PublicationDate`: written to the models of the file and its libraries.
  The XSLT uses the current time; set it for reproducible output.
- `XsltCompatibility`: reproduce the XSLT output exactly, including the bugs
  that the default fixes (see "Deviations").
- `Comments`: section comments in the output (on by default, not part of the graph).
- `Mode`: `AmlUaXslt` (the default, everything on this page) or
  `AnnexAInverse`, a second mode that is not a standard (see "Second mode").
- `NamespaceUri`: for `AnnexAInverse`, the model to write back.

## Namespaces and NodeIds

The namespace table is built as the XSLT builds it:

| Index | URI | Content |
|---|---|---|
| 1 | `http://opcfoundation.org/UA/AML/<FileName>` | the CAEXFile node, its properties and the collection folders |
| 2 | `http://opcfoundation.org/UA/AML/` | AML base types (`Opc.Ua.AMLBaseTypes.NodeSet2.xml`, draft of the working group) |
| 3... | one per library named in an `ExternalReference` alias, e.g. `.../AML/AutomationMLBaseRoleClassLib` | classes used through the alias |
| | `.../AML/AutomationMLBaseRoleClassLib`, `.../AML/AutomationMLInterfaceClassLib` | fallbacks, when the document never mentions AutomationMLBaseRole or AutomationMLBaseInterface |
| then | `http://opcfoundation.org/UA/AML/<Name>` | one per InstanceHierarchy and library of the document, in document order |

Aliases name the UA base types and reference types, the AML base types
(`CAEXObjectType` = `ns=2;i=1001`, `HasAMLRoleReference` = `ns=2;i=4001` and so
on) and every class used through an `ExternalReference` alias, e.g.
`AutomationMLBaseRoleClassLib/AutomationMLBaseRole/Resource` = `ns=3;s=Resource`.

`Models` holds one model per library and InstanceHierarchy (version from its
`Version` element) and one for the file (version 0.0.0) that requires them,
the UA base model 1.04.3 and the AML base types 1.0.1.

All NodeIds are strings, spaces removed:

| Node | NodeId |
|---|---|
| CAEXFile | `ns=1;s=CAEXFile` |
| its properties | `ns=1;s=CAEXFile_FileName`, `_SchemaVersion`, `_SuperiorStandardVersion`, `_AdditionalInformation`, `_SourceDocumentInformation` |
| collection folders | `ns=1;s=InstanceHierarchies`, `InterfaceClassLibs`, `RoleClassLibs`, `SystemUnitClassLibs`, `AttributeTypeLibs` |
| InstanceHierarchy | `ns=n;s=InstanceHierarchy_<Name>` |
| library | `ns=n;s=<kind>`, e.g. `ns=5;s=RoleClassLib` (one library per namespace) |
| class | `ns=n;s=<Name>` |
| InternalElement, ExternalInterface | `ns=n;s={<ID>}` (braces added to a bare GUID) |
| attribute | `ns=n;s=<owner>_<Name>`, owner being the class name, the element ID or the owning attribute's NodeId part |
| AML_ID, Version, AdditionalInformation, Copyright | `ns=n;s=<owner>_AML_ID`, `_Version`, `_AdditionalInformation_1`, `_Copyright` |
| constraint, required value | `ns=n;s=<attribute>_Constraint_1`, `..._Constraint_RequiredValue_2` |
| RefSemantic | `ns=n;s=<attribute>_RefSemantic` |

Because class NodeIds consist of the name, two classes of the same name in
one library share a NodeId.

## Mapping

| AML | UA |
|---|---|
| CAEXFile | Object `CAEXFile` of `CAEXFileType`, Organizes inverse from `AutomationMLFiles` |
| FileName, SchemaVersion, SuperiorStandardVersion | String properties of CAEXFile |
| AdditionalInformation, SourceDocumentInformation (file level) | String properties holding the element as XML text |
| InstanceHierarchy | Object of FolderType; Organizes from `InstanceHierarchies` and inverse from `AutomationMLInstanceHierarchies` |
| InterfaceClassLib, RoleClassLib, SystemUnitClassLib, AttributeTypeLib | Object of FolderType in its own namespace; Organizes from the collection folder and inverse from the base types' `*Libs` folder; Organizes to its top classes |
| InterfaceClass, RoleClass, SystemUnitClass | ObjectType; HasSubtype inverse from the base class (RefBaseClassPath), otherwise from `AutomationMLBaseInterface`, `AutomationMLBaseRole` or `CAEXObjectType`; Organizes inverse from the parent class or library |
| AttributeType | VariableType; HasSubtype inverse from its RefAttributeType or `AMLBaseVariableType` |
| InternalElement | Object; HasComponent from its parent; HasTypeDefinition to the SystemUnitClass (RefBaseSystemUnitPath), `CAEXObjectType` without one |
| mirror object (RefBaseSystemUnitPath = ID of the master) | IsAMLMirroredAs to the master, no HasTypeDefinition |
| ExternalInterface | Object; HasComponent from its parent; HasTypeDefinition to the InterfaceClass |
| InternalLink | HasAMLInternalLink between the two interfaces, forward from side A, inverse from side B |
| SupportedRoleClass, RoleRequirements | HasAMLRoleReference to the RoleClass |
| Attribute | Variable; HasComponent from its owner; HasTypeDefinition to its AttributeType or `AMLBaseVariableType`; DataType from the table below; Value from Value, else DefaultValue |
| Attributes of RoleRequirements | not exported |
| Constraint | Variable of `CAEXNominalScaledConstraintType`, `CAEXOrdinalScaledConstraintType` or `CAEXUnknownConstraintType`; HasAMLConstraint from the attribute |
| RequiredValue, RequiredMinValue, RequiredMaxValue, Requirements | Variables of `CAEXRequiredValue` and so on; HasAMLRequirement from the constraint |
| RefSemantic | Variable with CorrespondingAttributePath as value; HasAMLReferenceType from the attribute |
| ID | String property `AML_ID` (value in braces) of classes, InternalElements and ExternalInterfaces |
| Version, Copyright, AdditionalInformation | String properties of their owner |
| Description | Description; Documentation for libraries and classes (classes only in CAEX 2.15 documents with the XSLT, D2) |
| ExternalReference | namespace and aliases for the classes used through its alias |

Data types (DatatypeTranslation.xslt, first match wins):

| AML | UA | AML | UA |
|---|---|---|---|
| xs:string | String | xs:long | Int64 |
| xs:boolean | Boolean | xs:int, xs:integer | Int32 |
| xs:decimal | Decimal | xs:short | Int16 |
| xs:float | Float | xs:byte | SByte |
| xs:double | Double | xs:positiveInteger, xs:unsignedLong | UInt64 |
| xs:duration | Duration | xs:unsignedInt | UInt32 |
| xs:base64Binary | ByteString | xs:unsignedShort | UInt16 |
| xs:anyURI | QualifiedName | xs:unsignedByte | Byte |
| xs:date | DateTime | xs:normalizedString | NormalizedString (D9) |
| xs:token | Guid | xs:language | LocaleId |

Other types get no DataType (D9). Values are written with their own type for
String, Boolean, the integer types, Float, Double, Byte and SByte (D10), as
String otherwise.

## Verification

`ExportConformanceTests` runs the unit tests of AML-UA-XSLT (T00 to T13,
fifteen AML files, copied to `dotnet/OpcUaAml.Tests/Fixtures/aml-ua-xslt/`)
and compares our NodeSets with the XSLT output as graphs, using
`NodeSetComparer` (`dotnet/OpcUaAml.Core/Compare/`): namespace table in order,
aliases, models without PublicationDate, and per NodeId the node class,
BrowseName, DisplayName, Description, Documentation, ParentNodeId, DataType,
ValueRank, IsAbstract, Symmetric, InverseName, the value (XML text compared
structurally), the DataType definition with its fields (compared since
2026-09-19; the fifteen graphs stay equal) and the set of references, with aliases resolved and namespace
indexes replaced by URIs.

- Compatibility mode: all fifteen graphs are equal.
- Default: nine are equal; `1_AMLBaseLibraries`, `3_IE_Attribute`, `5_SUC`,
  `8_AMLAttributeLibrary`, `10_RefSemantic` and `11_Constraints` differ only by
  the deviations listed for them (D1, D2, D3, D5, D12). The test fails on any
  other difference and on a listed deviation that no longer shows.
- All outputs of both modes are valid against `UANodeSet.xsd` and load into
  NodeStates with the OPC UA .NET stack (`UANodeSet.Read`, `Import`).
- In the default output every reference into the document's own namespaces
  points to an existing node.

`ExportRuleTests` covers the deviations on `Fixtures/export/Deviations.aml`,
whose XSLT output (SaxonC-HE 13.0) is stored next to it; compatibility mode
reproduces it.

Beyond the tests, compatibility mode was compared with SaxonC-HE on 36 further
AML documents (AML examples of the AutomationML association and the OPC
Foundation, MTP manifests, AAS examples, the libraries of this workspace, up
to 3 300 nodes): no difference.

## Deviations from AML-UA-XSLT

The default output fixes evident bugs of the XSLT. `XsltCompatibility`
switches all of them off.

| | XSLT | Default | Unit tests |
|---|---|---|---|
| D1 | RefSemantic nodes have the DisplayName "RefSematic" | "RefSemantic" | T03, T10 |
| D2 | Tests `Version` and `Description` with unprefixed XPath names, which match only in CAEX 2.15 documents (no namespace). In CAEX 3.0 documents classes, InternalElements and ExternalInterfaces never reference their Version property, and classes lose their Documentation. | Both CAEX versions treated alike | T08 |
| D3 | GetObjectName lists `AttributeTypeClass`, no CAEX element, so the AML_ID and Version of an AttributeType with an ID are named after the ID while the class is named after its name: their ParentNodeId and the inverse HasComponent of its attributes point nowhere. Likewise an Attribute with an ID names its children by ID, while its own NodeId is built from owner and name, and its AML_ID is referenced but never created. | Named after the class, and after owner and name; the AML_ID of an Attribute is created | T08 |
| D4 | The inverse Organizes and a relative HasSubtype of a nested class use the parent's name with spaces, the parent's NodeId has them removed | Spaces removed | none |
| D5 | Every collection folder gets the descriptions of all libraries and hierarchies of the file as its Description | Descriptions of its own children only | T01, T08, T11 |
| D6 | A second SourceDocumentInformation, SuperiorStandardVersion or RefSemantic repeats the NodeId of the first | Numbered like AdditionalInformation (`_1`, `_2`) | none |
| D7 | Two libraries of the same name give two equal namespace URIs; the index lookup then concatenates both indexes (`ns=67`) | One URI, first index | none |
| D8 | The InternalLink partner is searched among all descendants of the partner element; several matches are joined into one NodeId, none gives `ns=n;s=`, always in the namespace of the near end | One reference per match, in the partner's namespace, none without a match | none |
| D9 | `xs:normalizedString ` carries a trailing space in the table and never matches; types missing from the table become the lower-cased XML Schema name (`datetime`), which is no UA data type; Decimal, NormalizedString and LocaleId have no alias | Trailing space ignored; no DataType for unknown types; aliases added when used (`i=50`, `i=12877`, `i=295`) | none |
| D10 | Float, Double, Byte and SByte values are written as `uax:String` although the DataType says otherwise; their DefaultValue is ignored | Written with their type, DefaultValue used like for String and integers | none |
| D11 | Tests for a constraint child `UnknownConstraint`; CAEX calls it `UnknownType`, so `CAEXUnknownConstraintType` is never used | `UnknownType` | none |
| D12 | Creates Version, Copyright and AdditionalInformation properties that no reference reaches: the Version of an InstanceHierarchy, every Copyright, and Copyright and AdditionalInformation of libraries and hierarchies (these also get a ParentNodeId that is not their owner's NodeId) | Referenced with HasProperty (HasComponent for AdditionalInformation) from the owner, parent is the owner | T05, T10, T11 |
| D13 | Splits class paths at the first `/`, so CAEX 3.0 bracket paths such as `[SUC_http://opcfoundation.org/UA/]/[BaseObjectType]` resolve to library `[SUC_http:` and no class; the HasSubtype and HasTypeDefinition references come out as `ns=;s=`. Every library generated by OPC 10000-83 Annex A is named after a namespace URI and is written this way | Bracket paths are split at `]/` into library and class chain | `ExportBracketPathTests`; found in the round trip of DI |
| D14 | Requires only the UA base model and `http://opcfoundation.org/UA/AML/`, although classes of imported libraries (the AutomationML base role, interface and system unit classes, and fallback libraries) are referenced in their own namespaces. A NodeSet importer does not know it has to load them and stops at the first unknown node (`Can't find node AutomationMLBaseRole`) | A RequiredModel per imported or fallback library; `nodesets/Opc.Ua.AMLStandardLibraries.NodeSet2.xml` provides the AutomationML standard libraries | `BundledNodeSetTests`; found in the round trip of `5_SUC.aml` |
| D15 | Drops `Attribute/@Unit`, although DIN SPEC 16592 maps it to a Property `Unit` and the draft base types declare `Unit` on `AMLBaseVariableType` | A Property `Unit` (String, PropertyType) below the attribute variable, BrowseName in the base types namespace | `D15_writes_the_unit_of_an_attribute_as_Unit_property`; found in the round trip and the DIN SPEC comparison |
| D16 | Exports a BPR 007 DataVariable (an attribute bound to a server node through `NodeId` and `RefDataSource`) as a plain `AMLBaseVariableType`, although the draft base types define `AMLOpcUaConnectionType` for it | Typed `AMLOpcUaConnectionType` with `VariableNodeId` (as `nsu=<URI>;…`, resolved through the server's NameSpaceTable), `ServerAddress` (EndpointURL, else DiscoveryURL) and `ServerAlias` (the server element's name); only when both Mandatory components can be filled and the attribute has no AttributeType. The BPR sub-attributes stay | `ExportConnectionTests` |

Kept as the XSLT does it, although questionable (open questions for the
working group):

- Library and class BrowseNames carry no namespace index, so they are in
  namespace 0.
- The InstanceHierarchies folder Organizes its hierarchies, while each
  hierarchy names the folder as source of an inverse HasComponent.
- Objects without HasTypeDefinition: mirror objects (IsAMLMirroredAs only),
  InternalElements whose SystemUnitClass is not in the document and not used
  through an alias, ExternalInterfaces of `AutomationMLInterfaceClassLib/AutomationMLBaseInterface`
  itself or of an InterfaceClass missing from the document. OPC UA requires a
  type definition for Objects.
- The CAEXFile references the collection folders only for libraries other
  than the AutomationML base libraries, although the folders exist.
- `xs:anyURI` maps to QualifiedName and `xs:token` to Guid (first match in the
  table; the table also lists UriString for `xs:anyURI`).
- Models: a library without Version gets the model version "" and the
  required model version "", one with Version 0 the versions "0" and "0.0.0".
- The file requires AML base types 1.0.1 of 2019-09-09; the base types NodeSet
  of the working group declares 1.00 of 2016-02-22.
- Values of DateTime, Guid, Duration, QualifiedName, Decimal and the like are
  written as String.

## OPC 30040 v1.00

A second mode following OPC 30040 v1.00 (2016) is not implemented. The
exporter keeps the rules in separate methods (namespaces, NodeIds, the
References template, data types), so such a mode would replace those. What
v1.00 does differently, from its text and the example in Annex A:

| Topic | OPC 30040 v1.00 | AML-UA-XSLT |
|---|---|---|
| Namespaces | Index 1 AML base types, one namespace per document (server specific URI, e.g. `http://www.iosb.fraunhofer.de/Topology.aml`), optionally `http://opcfoundation.org/UA/AML/AMLLibs/` for the standard libraries | Index 1 the file, 2 the base types, one namespace per library and InstanceHierarchy, one per imported library |
| NodeIds | Numeric (`ns=2;i=8`) | Strings from names and IDs |
| CAEXFile | Object named after the file, `CAEXFileType`, collection folders as HasComponent (Mandatory in the type) | Object `CAEXFile`, collection folders by Organizes, plus AttributeTypeLibs |
| Collections to libraries and hierarchies | HasComponent | Organizes |
| Library to class | Organizes (text), the Annex A example writes HasAMLInternalLink (`ns=1;i=4002`) | Organizes |
| Class without base class | HasSubtype from `CAEXObjectType` | from the alias of AutomationMLBaseInterface or AutomationMLBaseRole, `CAEXObjectType` for SystemUnitClasses |
| ID and Version | Properties `ID` and `Version` of `PropertyType` (`i=68`) | `AML_ID` and `Version` of `AMLBaseVariableType` |
| SchemaVersion | Property `CAEXSchemaVersion` | `SchemaVersion` |
| Attribute | Variable of `BaseDataVariableType` (`i=63`) | `AMLBaseVariableType` or the AttributeType's VariableType |
| Children | Inverse HasComponent to the parent on InternalElements, attributes and interfaces | Forward HasComponent from the parent; inverse only on attributes |
| Data types | Table 20: decimal Double, anyURI String, token String, integer Int64, positiveInteger Int64, dateTime DateTime, time Duration, hexBinary ByteString, QName String, NOTATION and IDREFS ListOfString | see the table above |
| CAEX 3.0 | Not covered: no AttributeTypeLib, constraints, RefSemantic, mirror objects, SourceDocumentInformation | Covered, with the reference types HasAMLReferenceType, IsAMLMirroredAs, HasAMLConstraint, HasAMLRequirement and the constraint VariableTypes of the draft base types |
| AutomationMLBaseInterface | ObjectType `ns=1;i=1002` of the base types | Dropped from the draft base types; comes from the AutomationMLInterfaceClassLib namespace |

## DIN SPEC 16592

DIN SPEC 16592:2016-12 extends OPC 30040 v1.00 and is the published reference
the AML-UA-XSLT rules revise. Where it differs from what the XSLT (and this
port) writes:

| Topic | DIN SPEC 16592 | AML-UA-XSLT and this port |
|---|---|---|
| Role assignment | Two ReferenceTypes, `HasAMLSupportedRoleClass` (inverse `IsSupportedRole`) and `HasAMLRoleRequirement` (inverse `IsRequiredRole`) | One `HasAMLRoleReference`, as in OPC 30040 |
| Attribute unit | Property `Unit` (String) | The XSLT drops it, although the draft base types declare `Unit` as an Optional property of `AMLBaseVariableType`; this port writes it by default (D15) |
| Attribute DefaultValue, RefSemantic, constraints | Properties `DefaultValue`, `RefSemantic`, constraint properties | Covered, with the draft base types' constraint VariableTypes and `HasAMLConstraint` |
| Links to other information models | `HasAMLUAReference` (inverse `IsAMLReferenceOf`) from an element whose ExternalDataConnector points at a node of another model, e.g. IEC 61131-3 | Not written |
| Entry points | `AutomationMLFiles` (file view), `AutomationMLInstanceHierarchies`, `AutomationMLLibraries` | `CAEXFile` with collection folders |
| Several documents | One ObjectType per class, whichever document holds it | One namespace per library; classes of other documents only through aliases (see Known limits) |
| UA configuration in AML (clause 6) | Server element with DiscoveryURL, EndpointURL, TransportProfileURI, SecurityPolicy, MessageSecurityMode, UserToken, NamespaceTable; variables reference the server element's ID and carry a NodeId | Not an export topic; the addressing in [servers](server.md) follows BPR 007 DataVariable, which covers DiscoveryURL, EndpointURL, SecurityPolicy and NamespaceTable |

The unit is written (D15), and bound attributes become `AMLOpcUaConnectionType` variables (D16). `HasAMLUAReference` and the split role references
are not: the draft base types this export requires define neither, so they
would need ReferenceTypes of our own; they are questions for the working
group.

## Second mode: the inverse of Annex A

The rules above export an AML document. An OPC UA model that the import put
into a document by OPC 10000-83 Annex A is AML too, so they export it as AML:
its Variables, Methods, DataTypes and ReferenceTypes become Objects and
ObjectTypes (chain A of the [round trip](roundtrip.md)). That is what the rules
say, and it is of no use to someone who wants the OPC UA model back.

`ExportMode.AnnexAInverse` (`AnnexAInverse.cs`) reads the Annex A libraries and
instances of one namespace and writes the nodes they came from:

| Annex A in AML | Written as |
|---|---|
| InterfaceClass in `ICL_<uri>`, the inverse as nested class | UAReferenceType with InverseName, Symmetric, IsAbstract |
| AttributeType in `ATL_<uri>` (not `ListOf…`) | UADataType with its Definition, EnumStrings, EnumValues or OptionSetValues, and new encodings for structures |
| SystemUnitClass in `SUC_<uri>` | UAObjectType, or UAVariableType when it derives from BaseVariableType |
| InternalElement of `UaMethodNodeClass` | UAMethod, with MethodDeclarationId found in the types of its holder |
| InternalElement with a `Value` attribute | UAVariable with DataType, ValueRank, ArrayDimensions and Value |
| other InternalElement | UAObject |
| `ModellingRule` of the child's interface | HasModellingRule |
| SupportedRoleClass or RoleRequirements to `RCL_<uri>` | HasInterface |
| ExternalInterface with `ReferenceIds` | the listed non hierarchical references |
| instances below the ns0 folders of the InstanceHierarchy | UAObject and UAVariable below Objects or Server |

NodeIds are the ones Annex A kept in the IDs and `NodeId` attributes, so the
result can replace the original. Values are written for built in types and
their subtypes, texts with their locale, NodeIds, enumerations (Annex A
writes the name, OPC UA the number), option sets (Annex A writes one boolean
per option), Arguments, and structures of the namespace itself or of a
bundled model (UA, DI), whose XML encodings are known. A DisplayName that
differs from the BrowseName comes back; a node that two parents hold is
written once with a reference from each. Children that Opc2Aml repeats below typed declarations
are recognised by their NodeId and written once.

```bash
uaaml export Opc.Ua.Di.NodeSet2.xml.amlx -o DI.NodeSet2.xml --annex-a-inverse
uaaml export plant.aml -o fx-ac.xml --annex-a-inverse --namespace http://opcfoundation.org/UA/FX/AC/
```

Without `--namespace` the document must hold the libraries of exactly one
namespace besides the UA base model; otherwise the export names the
candidates. In the plugin, the export dialog offers both modes and lists the
namespaces.

What does not come back is what Annex A does not carry into AML. The round
trip measures it node by node (chain C in [roundtrip](roundtrip.md)):

- Documentation, type dictionaries and their variables, and nodes no type or
  folder holds.
- The original NodeIds of encodings; structures get new ones.
- Descriptions of DataTypes in some models, of option set bits, and the field
  definitions of some option sets.
- Empty values and matrices (Annex A writes an empty Value attribute for
  them and for no value alike), and values of structures from models that
  are neither the exported one nor bundled.
- References a declaration has to declarations of another type, for example to
  the placeholders of its own type definition: in AML they look like the
  children Opc2Aml repeats below every typed declaration.
- The DataType of a VariableType without a `Value` attribute (DI 468).

## Known limits

- Classes referenced but not contained in the document are only known through
  `ExternalReference` aliases; there is no library catalog as for the import.
- A class path is resolved by name only (GetClass): the first class of that
  name, with all libraries of the same name searched together.
- The export reads the CAEX XML. An `.amlx` container is read through
  Aml.Engine; only its root document is exported.
- Size: the AML libraries of UA and DI (`Opc.Ua.Di.NodeSet2.xml.amlx`) give
  285 000 nodes in 13 seconds; the whole NodeSet is built in memory.
- Writing `.amlx` is not supported.
- An ExternalDataConnector's `refURI` (a reference into another file) gives
  no reference. Wassilew et al. (2016, 2017) propose `HasAMLExternalLink` for
  it; the draft base types have no such ReferenceType.
- Variables are `AMLBaseVariableType` or the AttributeType's VariableType.
  The DataAccess types of Part 8 (AnalogItemType, TwoStateDiscreteType,
  MultiStateDiscreteType), chosen by data type as Wassilew et al. do, would
  depart from both the XSLT and DIN SPEC 16592; not done.
- Deliberately not done, because the draft base types define neither and the
  choice belongs to the working group: `HasAMLUAReference` (bound attributes
  get `AMLOpcUaConnectionType` instead, D16) and DIN SPEC 16592's split into
  `HasAMLSupportedRoleClass` and `HasAMLRoleRequirement`.
