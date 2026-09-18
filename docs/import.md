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

When two files declare the same model, the newer publication wins; at the same
date the first one found wins. Missing models are named before Opc2Aml runs.

## Merging into a document

Libraries are the unit of the merge. A library the document does not have is
added. A generated library (`ATL_`, `ICL_`, `RCL_`, `SUC_` prefix) the document
already has is replaced at the same position, unless

- the document's copy was generated from a newer publication of the model
  (it is kept, with a note), or
- "Replace existing" / `--keep` says to keep existing libraries.

The AutomationML base libraries are never replaced. IDs are kept as Opc2Aml
wrote them, so a second import of a namespace produces the same IDs and
references into the library stay valid.

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
- **CAEX 2.15 documents** are refused: the Annex A libraries use CAEX 3.0
  features (AttributeTypeLib, nested attribute types).
- **Duration.** A conversion reads the whole UA base model and takes 15 to 25
  seconds. The plugin runs it in the background.
