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
uaaml roundtrip Opc.Ua.Di.NodeSet2.xml --inverse -o report.md
```

A `.xml` file runs chain A, any other file runs chain B; `--inverse` runs a
NodeSet through chain C:

| Chain | Steps | Matched by |
|---|---|---|
| A | NodeSet, Annex A import, export, NodeSet | BrowseName of types and their declarations in the file's own model |
| B | AML document, export, Annex A import, AML document | path of InternalElements; library and name of classes |
| C | NodeSet, Annex A import, export by the inverse of Annex A, NodeSet | NodeId: every fact of every node of the file's own model |

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

### Chain C: NodeSet to AML and back by the inverse of Annex A

Chain A shows that the two standards do not form a pair. Chain C replaces the
way back with the inverse of Annex A, a second export mode that is not a
standard ([export](export.md#second-mode-the-inverse-of-annex-a)). It compares
the result with the original node by node (`NodeSetComparer.Flatten`): a fact
is kept when the same NodeId has the same value.

A lost fact counts as lost on the way in (Annex A) when the AML shows that it
never arrived there: the node is not in the AML, a reference type appears on
neither end, the Value attribute is empty (Opc2Aml writes it so for no value,
an empty one and a matrix alike), a VariableType has no Value attribute (its
DataType is the attribute's type), the element has no Description or the
DataType no field definitions, or only the description of a field is missing.
Documentation is never carried. The rest are losses of the inverse itself,
given in brackets.

| Criterion | DI | Safety | FX Data | FX AC | FX CM |
|---|---:|---:|---:|---:|---:|
| Nodes present, with their node class | 413/447 (0) | 85/91 (0) | 65/186 (0) | 432/511 (0) | 414/545 (0) |
| BrowseName and DisplayName kept | 411/447 (0) | 85/91 (0) | 65/186 (0) | 432/511 (0) | 414/545 (0) |
| Description kept | 16/16 | 37/40 (0) | 1/7 (0) | 5/11 (0) | 0/4 (0) |
| ParentNodeId kept | 345/360 (0) | 73/74 (1) | 32/82 (0) | 378/437 (0) | 343/397 (0) |
| DataType kept | 231/246 (0) | 66/66 | 25/77 (0) | 272/311 (0) | 222/278 (0) |
| ValueRank and ArrayDimensions kept | 145/145 | 14/14 | 36/36 | 221/242 (0) | 272/272 |
| IsAbstract, Symmetric, InverseName kept | 28/28 | 2/2 | 7/7 | 20/20 | 23/23 |
| MethodDeclarationId kept | 28/28 | | 6/6 | 27/35 (0) | 14/14 |
| Values kept | 96/111 (0) | 12/13 (0) | 18/71 (0) | 73/108 (1) | 56/115 (2) |
| DataType definitions kept, per field | 29/36 (0) | 16/22 (1) | 113/125 (3) | 37/48 (0) | 230/244 (4) |
| References kept | 1448/1539 (1) | 220/236 (4) | 128/581 (0) | 1528/1826 (0) | 1433/1940 (2) |
| Documentation kept | 0/79 | 0/15 | 0/33 | 0/64 | 0/45 |

What Annex A does not carry, found in the AML Opc2Aml writes:

- Documentation; type dictionaries and their variables; encodings; nodes no
  type or folder holds (most of FX Data).
- Descriptions of some DataTypes and of option set bits; the field definitions
  of some option sets.
- Values that are empty, and matrices: the Value attribute stays empty either way.
- The DataType of a VariableType without a Value attribute (DI 468).
- Some references between declarations, for example to the placeholders of a
  declaration's own type definition (Safety 5000 to 6029), and references
  between declarations that Annex A does not write at all (FX CM 4001,
  HasCause 53, HasDictionaryEntry).

What the inverse loses itself is small: a ParentNodeId that Safety writes for
an organized node and DI does not (the AML cannot tell them apart), and a few
references and definition fields. Methods, DataTypes and ReferenceTypes, 0 %
in chain A, come back completely.

#### What the percentage does not count

The figures above count the facts `NodeSetComparer` compares: node class,
BrowseName, DisplayName, Description, ParentNodeId, DataType, ValueRank and
ArrayDimensions, IsAbstract, Symmetric, InverseName, MethodDeclarationId,
values, definition fields, references and Documentation. Attributes neither
Annex A nor the comparison carries are not in the denominator:
`Category`, `SymbolicName`, `ReleaseStatus`, `AccessLevel`,
`UserAccessLevel`, `Historizing`, `WriteMask`, `UserWriteMask`,
`AccessRestrictions`, `RolePermissions`, `Executable`, `DataTypeVersion`,
the root `Extensions`, the locale of a node's texts, and the version a
`RequiredModel` asks for. A reader who wants a number for "everything in the
file" has to add those.

Two losses the import now names rather than leaves to be discovered: a child
that hangs off its type only through `Organizes` or a reference type the model
defines itself (DI has two, AutoID two, 122 in the corpus, each taking its
subtree with it), and the instances of the model, which stay in the converted
document's instance hierarchy and are not merged into the target.

#### Corpus of companion specifications

The same chain over 34 companion specifications of
[OPCFoundation/UA-Nodeset](https://github.com/OPCFoundation/UA-Nodeset)
(commit `4b79bcf`, 2026-08-18; newest version where a model has several),
every fact of every node of the model counted:

| Model | Nodes kept | Facts kept | Lost by Annex A | Lost by the inverse |
|---|---:|---:|---:|---:|
| AMB | 80/92 | 664/804 (83 %) | 139 | 1 |
| AutoID | 185/305 | 1764/2895 (61 %) | 1130 | 1 |
| CommercialKitchenEquipment | 793/797 | 7288/7406 (98 %) | 118 | 0 |
| CranesHoists | 88/89 | 690/748 (92 %) | 48 | 10 |
| Gds | 355/405 | 3236/3766 (86 %) | 522 | 8 |
| Glass.v2 | 110/149 | 1052/1380 (76 %) | 327 | 1 |
| gms | 246/273 | 2152/2410 (89 %) | 255 | 3 |
| IA | 111/122 | 960/1099 (87 %) | 139 | 0 |
| Ijt.Base | 617/782 | 6015/7760 (78 %) | 1708 | 37 |
| Ijt.Tightening | 30/36 | 305/372 (82 %) | 67 | 0 |
| isa95-jobcontrol | 199/258 | 2104/2625 (80 %) | 510 | 11 |
| LADS | 624/650 | 5097/5451 (94 %) | 318 | 36 |
| LaserSystems | 275/287 | 2337/2721 (86 %) | 377 | 7 |
| Machinery | 180/180 | 1683/1772 (95 %) | 89 | 0 |
| Machinery.Examples | 293/514 | 2744/4900 (56 %) | 2143 | 13 |
| Machinery.Energy | 93/112 | 864/956 (90 %) | 92 | 0 |
| Machinery.Jobs | 40/60 | 383/566 (68 %) | 183 | 0 |
| Machinery.ProcessValues | 138/138 | 1266/1280 (99 %) | 9 | 5 |
| Machinery_Result | 77/124 | 732/1212 (60 %) | 453 | 27 |
| MachineTool | 560/589 | 5029/5430 (93 %) | 398 | 3 |
| MachineVision | 718/790 | 7194/7958 (90 %) | 746 | 18 |
| MetalForming | 151/155 | 1298/1368 (95 %) | 57 | 13 |
| PackML | 220/248 | 1988/2265 (88 %) | 277 | 0 |
| IRDI | 271/271 | 1387/1389 (100 %) | 2 | 0 |
| PADIM | 786/788 | 6586/7338 (90 %) | 750 | 2 |
| PlasticsRubber.GeneralTypes | 907/961 | 8651/9255 (93 %) | 589 | 15 |
| PlasticsRubber.IMM2MES | 317/321 | 3198/3300 (97 %) | 79 | 23 |
| Pumps | 9585/9624 | 91973/92552 (99 %) | 524 | 55 |
| Robotics | 461/531 | 3787/4457 (85 %) | 667 | 3 |
| Scales | 1244/1348 | 11434/12533 (91 %) | 1062 | 37 |
| TMC | 9272/9376 | 87783/89068 (99 %) | 1088 | 197 |
| Weihenstephan | 59/59 | 423/440 (96 %) | 17 | 0 |
| Woodworking | 1802/1816 | 16759/16954 (99 %) | 195 | 0 |
| Eumabois | 212/212 | 2025/2044 (99 %) | 19 | 0 |
| **All 34** | 31099/32462 | 290851/306474 (94.9 %) | 15097 | 526 (0.17 %) |

Four of them first stopped inside Opc2Aml, which patches 0005 to 0008 fix
(see [UPSTREAM.md](../third_party/Opc2Aml/UPSTREAM.md)): a structure that
contains itself (Glass v2, a stack overflow that would end the editor), a
reference to a dictionary entry no model defines (MetalForming, MachineVision),
and a symmetric reference type (MachineVision). The inverse needed DisplayNames
that differ from the BrowseName, locales of texts, NodeId values, nodes held by
two parents, the "ThreeD" XML names of the base model's 3D types, models with
instances only (IRDI), and an index of interfaces for models with tens of
thousands of links (TMC, Pumps).

Still open, losses of the inverse above ten in one model: MethodDeclarationId
in TMC (96), ParentNodeId in TMC (58), Scales (36) and Machinery Result (20),
references in Pumps (48) and LADS (30), values in TMC (23) and MachineVision (17).

The run takes about half an hour the first time and under a minute from the
conversion cache:

```bash
uaaml roundtrip <NodeSets> --search <their folders> --inverse -o corpus.md
```

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
- Units were lost (0/1 in `5_SUC`, `6_RCL`, `7_ICL`; 0/5 in `13_Facet`),
  because the XSLT drops `Attribute/@Unit`. Since D15 the export writes a
  `Unit` property, and all units are recoverable (1/1 and 5/5). They come
  back as a child element `Unit` whose value is the unit, not as the CAEX
  `Unit` of the attribute: Annex A turns every property into an element.
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
| Export and AML-UA-XSLT | D15: units dropped | [export](export.md) |
| NodeSetComparer | MethodDeclarationId and NodeIds inside values compared as text, with namespace indexes | resolved by namespace URI; an encoding in a TypeId compares as the DataType it encodes |
| AML-UA-XSLT | Mirror objects without HasTypeDefinition | open, question for the working group |

Chain B needs two NodeSets that the working group does not publish as files:
the AML base types (taken from the AML-UA-XSLT unit tests) and the AutomationML
standard libraries (generated by our export). Both are bundled, see
[nodesets/README.md](../nodesets/README.md).
