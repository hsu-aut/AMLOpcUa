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

A file in these folders that cannot be read (another program holds it, no
read rights) is skipped with a warning of the catalog; the rest is used. A
NodeSet without a `Models` element (allowed before 1.04) stands for the first
of its `NamespaceUris` and requires the base model, as Opc2Aml reads it.

Without an account the plugin fetches the NodeSets the OPC Foundation
publishes on GitHub, [OPCFoundation/UA-Nodeset](https://github.com/OPCFoundation/UA-Nodeset),
branch `latest`: every released companion specification (**Companion
specs…**, `OpcFoundationNodeSets`, `uaaml opcf`). The repository's folders do
not name the namespaces reliably, so an index records the models each file
declares: the file list comes from GitHub's API in one call (60 calls an hour
are allowed without login), and of each file only its beginning up to
`</Models>` is read, by a Range request. The index is kept in
`%LOCALAPPDATA%\AMLOpcUa\opcfoundation` with each file's blob SHA and asked
again at most once a day; then only files that changed are read (143 files in
about seven seconds the first time). A chosen model is downloaded with every
model it requires that the catalog lacks. When GitHub cannot be reached or the
hourly limit is spent, the index on disk still serves. Tests run against a fake
of the tree API and the raw files.

The plugin can also fetch NodeSets from the OPC Foundation's
[UA Cloud Library](https://uacloudlibrary.opcfoundation.org) (**Cloud
Library…**, `CloudLibraryClient`): search, then download the chosen model and
every required model the catalog lacks into
`%LOCALAPPDATA%\AMLOpcUa\cloudlibrary`, which is searched on import. The
library needs an account or an API key; the password or key is kept for the
editor session only. **Publish…** in a namespace's details, and `uaaml cloud
upload`, send the NodeSet the namespace came from (found in the modeler's
models or the NodeSet folders) with what the library requires: title,
description, copyright text, license (MIT, ApacheLicense20 or Custom),
keywords and a documentation URL, by `PUT /infomodel/upload` as the library's
server source reads it. The OPC Foundation reviews an upload before it is
listed; the plugin asks once more before it sends. Tests run against a fake of
its REST API (v1, `/infomodel/find2`, `/infomodel/download/{id}` and
`/infomodel/upload`), since the real service cannot be used without
credentials, and an upload would publish.

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

Inside the AutomationML Editor a replacement needs care. The editor keeps
automatic reference updates on for every open document (Aml.Engine's lookup
service): when an element goes, every reference to it is rewritten, and one
whose target is gone is cleared. Removing the old library before the new one
arrives cleared the type of every instance of it, and each cleared reference
passed the editor's undo history and bindings; removing the UA base library
took 43 s. The merge therefore removes all old libraries first and inserts
the new ones afterwards, at the old places, with the updates held and the
document out of the lookup service's registry (`EditorServices.Hold`), so the
service builds its tables again in one pass the next time it is asked. The
paths stay the same, so nothing needs rewriting. Removing a namespace and
replacing the instance hierarchy the modeler fills work the same way. The
test `EditorServicesMergeTests` registers the service as the editor does,
with the editor's version of it (Aml.Engine.Services 4.3.1).

Opc2Aml brings its own copy of the base libraries (2.10.0). A document that
gets them through an `ExternalReference` instead, as the AutomationML Editor
sets it up (for instance `AutomationML_Base_Libraries_AMLEd2_2.11.0.aml`),
keeps that reference: the copy is left out, and the imported classes point at
the referenced library as `Alias@Library/Class`. Without this the document
held two versions of the same library, and the editor reported libraries
that cannot be verified. The referenced file is looked for next to the
document; it is used only when it holds every class the import points at,
otherwise the copy is added and the report names the classes it lacks.

## Missing models and removing a namespace

Before converting, the plugin checks that every model a NodeSet requires is
in its folder, the NodeSet folders or the plugin's own folders (models from
the modeler, the OPC Foundation, the Cloud Library and servers). If not, a
dialog names the missing models and offers to fetch them from the OPC
Foundation's published NodeSets (at once, by their URIs), to add a folder or to
search the Cloud Library, then tries again.

`NamespaceInspector` tells what a namespace contributes: ObjectTypes and
VariableTypes (SUC_), DataTypes (ATL_, without the `ListOf` array types),
ReferenceTypes (ICL_), the namespaces it builds on (any path of its libraries
into another's) and the elements typed by it. `Remove` deletes its four
libraries, and refuses while another namespace builds on it, an element uses
its types, any other path of the instance hierarchies leads into its
libraries (an interface, an attribute, a role), or a library of no namespace,
the user's own, refers to them. The metamodel libraries of Annex A
(`…_OpcAmlMetaModel`) are such libraries and keep the base model. Checked
with DI: 42, 2, 9 and 5, as its NodeSet declares.

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
- **IDs while converting.** For the conversion the importer puts its own ID
  service in Aml.Engine's process-wide service locator (`ConversionIds`, a
  sixth faster). Anything else in the same process gets its IDs from it in
  those seconds too: unique GUIDs as before, and none only for elements
  without an ID. The earlier service comes back at the end, unless another
  one was registered meanwhile. In the plugin that process is the converter's,
  not the editor's (next point).
- **The plugin converts in a process of its own.** It runs the converter it
  brings (`converter\uaaml.exe convert`, `ConverterProcess`) and reads the
  result back (`ConversionTransfer`: the document as AML, the rest as JSON
  beside it). Inside the AutomationML Editor, Aml.Engine is the editor's, in
  its version and with the services it registers for the whole process. Its
  undo service recorded every change of the converter's documents (1.5
  million for a small model with the UA base model) and the editor answered
  each one on its UI thread; its unique name service rewrote the references
  of the whole document on every inserted element, and can rename an element
  that has the name of an inherited one, which Annex A uses to override it.
  An import of Machinery that takes 21 s on the command line had not finished
  after half an hour and froze the editor. In a process of its own the
  conversion is the one the command line and the tests run, with the
  Aml.Engine it was built against; a converter that crashes (a stack
  overflow, say) no longer ends the editor, and its memory is given back.
  Opc2Aml patch 0011 keeps the converter's documents out of a registered undo
  service anyway, for any other host.
- **One at a time.** Aml.Engine keeps static state while Opc2Aml copies
  classes and resolves paths; two conversions at once, or a document loaded
  during one, failed with "Collection was modified". Conversions and document
  loads therefore share a lock in the process; cached results are read
  under it too, briefly.
- **The result is a file, not a container.** Opc2Aml wrote an `.amlx`, whose
  `AutomationMLContainer.AddRoot` lives in Aml.Engine; the AutomationML Editor
  brings its own Aml.Engine along, where that signature differs, and inside
  the editor every conversion failed with "Method not found". Patch 0010
  writes `<name>.aml` instead, which is all the importer and the cache ever
  read.
- **Cache.** A conversion is kept in `%LOCALAPPDATA%\AMLOpcUa\conversions`
  (`ConversionCache`, the last 40), under a key made of the content of the
  NodeSet and every file it requires, the build of Opc2Aml and a format
  number for how the import runs it. Converting the same files again takes under a second
  (DI: 0.7 s instead of 15 s). `NodeSetImporter.Cache = null` turns it off;
  the plugin empties it with Settings › Forget earlier conversions. An entry
  that cannot be read, whatever the zip or XML reader throws, is converted
  again and replaced; temporary files and containers a crash left behind are
  removed after an hour.
- **In the plugin** one import runs at a time; a second one started meanwhile
  is told to wait. A conversion whose document was closed or replaced while it
  ran is not merged anywhere.
