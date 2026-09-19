# Import: OPC UA NodeSets to AML libraries

## Mapping

The import follows OPC 10000-83 (UAFX Offline Engineering) v1.00.03,
Annex A "OPC UA Metamodel Mapping to AML for configuration" (normative). The
mapping is done by Opc2Aml; this project decides where NodeSets come from and
how the result enters a document.

| UA | AML |
|---|---|
| DataType | AttributeType in `ATL_<namespace URI>` |
| ObjectType, VariableType, Method | SystemUnitClass in `SUC_<namespace URI>` |
| InterfaceType | RoleClass in `RCL_<namespace URI>` and SystemUnitClass |
| ReferenceType | InterfaceClass in `ICL_<namespace URI>` (a pair for non-symmetric types) |
| Reference between instance declarations | ExternalInterfaces and an InternalLink |
| ModellingRule | Attribute `ModellingRule` on the target interface |
| NodeId | ID `nsu=<namespace URI>;<id>`, URL-encoded, and a NodeId attribute |

The result is CAEX 3.0. Every generated library carries an `OpcUaLibInfo`
block (Annex K) with namespace URI, model version and publication date.

## Where NodeSets come from

An import names one NodeSet. Every model it requires, directly or
transitively, must be available as a file:

1. the folder of the imported NodeSet,
2. the folders given with `--search` or under "NodeSet folders",
3. the NodeSets shipped in `nodesets/` (UA base model 1.05.07, DI 1.05.0).

The plugin can also fetch NodeSets from the OPC Foundation's
[UA Cloud Library](https://uacloudlibrary.opcfoundation.org) (**Cloud
Library…**, `CloudLibraryClient`): search, then download the chosen model and
every required model the catalog lacks into
`%LOCALAPPDATA%\AMLOpcUa\cloudlibrary`, which is searched on import. The
library needs an account or an API key; the password or key is kept for the
editor session only. Tests run against a fake of its REST API (v1,
`/infomodel/find2` and `/infomodel/download/{id}`), since the real service
cannot be used without credentials.

When two files declare the same model, the newer publication wins; at the same
date the first one found wins. Missing models are named before Opc2Aml runs.

## Merging into a document

Libraries are the unit of the merge. A library the document does not have is
added. A generated library (`ATL_`, `ICL_`, `RCL_`, `SUC_` prefix) the document
already has is replaced at the same position, unless

- the document's copy was generated from a newer publication of the model
  (it is kept, with a note), or
- "Replace existing libraries" is off (menu Settings) / `--keep` says to keep
  existing libraries.

The AutomationML base libraries are never replaced. IDs are kept as Opc2Aml
wrote them, so a second import of a namespace produces the same IDs and
references into the library stay valid.

## Missing models and removing a namespace

Before converting, the plugin checks that every model a NodeSet requires is
in its folder, the NodeSet folders or the plugin's own folders (models from
the modeler, the Cloud Library and servers). If not, a dialog names the
missing models and offers to add a folder or to fetch them from the Cloud
Library, then tries again.

`NamespaceInspector` tells what a namespace contributes: ObjectTypes and
VariableTypes (SUC_), DataTypes (ATL_, without the `ListOf` array types),
ReferenceTypes (ICL_), the namespaces it builds on (any path of its libraries
into another's) and the elements typed by it. `Remove` deletes its four
libraries, and refuses while another namespace builds on it or an element
uses its types. Checked with DI: 42, 2, 9 and 5, as its NodeSet declares.

## Verification

`OracleTests` converts DI 1.04.0 with UA 1.05.05, the inputs the OPC Foundation
used for `Opc.Ua.Di.NodeSet2.xml.amlx` in UA-Nodeset tag
`UAFX-1.00.04-2026-07-22` (both versions are recorded in that file), and
compares the result with it.

- **Class skeleton** (libraries, classes, derivation, child elements): equal
  except for 20 OptionSet attribute types, which the newer Opc2Aml gives their
  integer data type and the older one left empty.
- **Class IDs**: equal for all shared classes. This matters because patch 0002
  rewrote the ID formatting for the newer OPC UA stack.
- **Attributes**: about 49 000 differences, all explained by the generator
  version. The published file comes from Opc2Aml writer version 1.2.9336
  (July 2025), the vendored source is from January 2026. The newer version
  adds `EnumStrings`, `EnumValues`, `OptionSetValues` and reference interfaces
  on state machine transitions, and no longer writes `StructureFieldDefinition`
  below structure fields. The test reports these but does not fail on them.

The FX libraries in the same tag are not usable as an oracle: they were not
regenerated for the NodeSets of that tag. `FxTimeUnitsEnum`, for instance,
sits in the FX AC library (`nsu=http://opcfoundation.org/UA/FX/AC/;i=3006`),
while the FX NodeSets of the tag define it in FX Data.

## Known limits

- **DI 1.05.0 `ConnectsTo` placeholders.** DI 1.05.0 made `ConnectsTo` a
  non-hierarchical reference. The placeholders `<CPIdentifier>` under
  `NetworkType` and `<NetworkIdentifier>` under `ConnectionPointType` hang off
  their type only through it, so Opc2Aml has no element for them. Unpatched
  Opc2Aml fails on every NodeSet that uses DI 1.05.0; with patch 0001 the two
  references are skipped and reported as warnings.
- **CAEX 2.15 documents** cannot take the libraries: they use CAEX 3.0
  features (AttributeTypeLib, nested attribute types). `CaexUpgrade` converts
  a document with Aml.Engine's own transformation; `uaaml import --into`
  does so on its own, the plugin offers "Save a CAEX 3.0 copy…" (the editor
  lets plugins not replace its open document).
- **Duration.** Opc2Aml turns the whole UA base model into libraries on every
  conversion: 11 to 15 seconds for a small NodeSet, in the background in the
  plugin. Two changes took about 15 % off: while a conversion runs, Aml.Engine
  gets an ID service that does not create a GUID for every copied Attribute and
  Value (`ConversionIds`), and patch 0005 compares IDs ordinally. Most of the
  rest is spent inside Aml.Engine (service lookups, path queries, class
  instances).
- **Cache.** A conversion is kept in `%LOCALAPPDATA%\AMLOpcUa\conversions`
  (`ConversionCache`, the last 40), under a key made of the content of the
  NodeSet and every file it requires, the build of Opc2Aml and a format
  number for how the import runs it. Converting the same files again takes under a second
  (DI: 0.7 s instead of 15 s). `NodeSetImporter.Cache = null` turns it off;
  the plugin empties it with Settings › Forget earlier conversions.
