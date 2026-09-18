# Round trip: both mappings and back

Two standardized mappings connect OPC UA and AutomationML, one per direction:

- UA to AML: OPC 10000-83 Annex A, implemented by Opc2Aml ([import](import.md)).
- AML to UA: the rules of the AutomationML and OPC Foundation working group,
  AML-UA-XSLT, which revise OPC 30040 ([export](export.md)).

Nobody designed the two as a pair. The round trip sends a model through one
mapping and back through the other, and it reports per concept what arrives.

```bash
uaaml roundtrip Opc.Ua.Di.NodeSet2.xml --search nodesets -o report.md
uaaml roundtrip plant.aml VDI3682_Lib_v0.1.aml -o report.md
```

A `.xml` file runs chain A, any other file runs chain B:

| Chain | Steps | Matched by |
|---|---|---|
| A | NodeSet, Annex A import, export, NodeSet | BrowseName of types and their declarations in the file's own model |
| B | AML document, export, Annex A import, AML document | path of InternalElements; library and name of classes |

A step that fails does not stop the run: the report names the step and the
error, and the command returns 1. The code is in
`dotnet/OpcUaAml.Core/Roundtrip/`; `RoundtripTests` pins the numbers below, so
a change in either mapping shows up as a failing test.

## Corpus

Run on 2026-09-19 over the bundled NodeSets, the UAFX 1.00.04 fixtures, the 15
AML-UA-XSLT test documents, the ISO_PT domain library v0.3 of AMLPetriNet and
the VDI 3682 library v0.1.

### Chain A: NodeSet to AML to NodeSet

| Criterion | DI | Safety | FX Data | FX AC | FX CM |
|---|---:|---:|---:|---:|---:|
| Types present, by name | 44/44 | 6/6 | 1/1 | 28/28 | 24/24 |
| Type keeps its node class | 42/44 | 6/6 | 1/1 | 27/28 | 22/24 |
| Supertype kept | 44/44 | 6/6 | 1/1 | 28/28 | 24/24 |
| Original NodeId recoverable | 44/44 | 6/6 | 1/1 | 28/28 | 24/24 |
| Instance declarations present | 174/174 | 34/34 | 2/2 | 138/138 | 80/80 |
| Declaration keeps its node class | 59/174 | 4/34 | 0/2 | 47/138 | 19/80 |
| Declaration keeps its type definition | 151/151 | 32/32 | 2/2 | 126/126 | 77/77 |
| ModellingRule as HasModellingRule | 0/148 | 0/34 | 0/2 | 0/138 | 0/73 |
| ModellingRule recoverable (attribute) | 148/148 | 34/34 | 2/2 | 138/138 | 73/73 |
| Methods stay Methods | 0/23 | 0/2 | | 0/12 | 0/3 |
| DataTypes stay DataTypes | 0/9 | 0/5 | 0/29 | 0/12 | 0/33 |
| ReferenceTypes stay ReferenceTypes | 0/5 | | | 0/13 | 0/12 |

What survives is everything the AML side can hold as a name, a class or an
attribute: the type hierarchy, the declarations with their type definitions,
the original NodeIds and the ModellingRules as attribute values. What is lost
is the OPC UA meta model:

- Variables and VariableTypes become Objects and ObjectTypes. Annex A turns a
  Variable into an InternalElement with a Value attribute; the export turns
  every InternalElement into an Object.
- Methods become Objects, for the same reason.
- DataTypes become VariableTypes: Annex A makes them AttributeTypes, the export
  makes every AttributeType a VariableType.
- ReferenceTypes become ObjectTypes: Annex A makes them InterfaceClasses, the
  export makes every class an ObjectType.
- No ModellingRule arrives as a HasModellingRule reference. The export has no
  concept for it; the rule is still readable as the `ModellingRule` attribute.

A NodeSet that went through AML is therefore no substitute for the original.
The import keeps the NodeIds, so an AML model can always be related to the
original NodeSet, which is what [instances](instances.md) and
[servers](server.md) rely on.

### Chain B: AML to NodeSet to AML

| Document | Complete | Findings |
|---|---|---|
| 0 to 11 and 13 of AML-UA-XSLT | yes | InternalElements, their classes and attributes, class names, IDs and base classes arrive |
| 12_MirrorObject | no, stops at the Annex A import | the rules give a mirror object no HasTypeDefinition; Opc2Aml requires one |
| VDI3682_Lib_v0.1 | yes | 39/39 elements, 22/22 classes with IDs; 9/22 keep their kind |
| ISO_PT_DomainLibrary_v0.3 | no, stops at the Annex A import | requires the models of the object reference and OMG DD attribute type libraries, which have no NodeSet yet |

Across all documents:

- Every RoleClass, InterfaceClass and AttributeType comes back as a
  SystemUnitClass (0/5 in `6_RCL`, 0/5 in `7_ICL`, 0/22 in
  `1_AMLBaseLibraries`, 13 of 22 in the VDI 3682 library). Annex A knows OPC UA
  types only, and every type becomes a SystemUnitClass; the kind survives only
  in the library name (`SUC_http://opcfoundation.org/UA/AML/RoleClassLib1`).
- Units are lost (0/1 in `5_SUC`, `6_RCL`, `7_ICL`; 0/5 in `13_Facet`). The
  export writes no EngineeringUnits.
- InstanceHierarchies come back as an element below `OPC UA Instance
  Hierarchy`, not as hierarchies of their own.

Not measured yet: ExternalInterfaces and InternalLinks of instances, role
requirements, and SupportedRoleClasses.

## Defects found by the round trip

| Where | Defect | Fix |
|---|---|---|
| Opc2Aml | Endless recursion when ordering the model information of the exported NodeSet | Patch 0003 |
| Opc2Aml | ObjectTypes below Objects counted as instances | Patch 0004 |
| Export | D13: CAEX 3.0 bracket paths not resolved | [export](export.md) |
| Export | D14: models of imported AML libraries not required | [export](export.md), `Opc.Ua.AMLStandardLibraries.NodeSet2.xml` |
| AML-UA-XSLT | Mirror objects without HasTypeDefinition | open, question for the working group |

Chain B needs two NodeSets that the working group does not publish as files:
the AML base types (taken from the AML-UA-XSLT unit tests) and the AutomationML
standard libraries (generated by our export). Both are bundled, see
[nodesets/README.md](../nodesets/README.md).
