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
is kept when the same NodeId has the same value. A lost fact of a node that
Annex A did not carry into AML at all, or a reference to such a node, is
counted as lost on the way in; the table gives the losses of the inverse
itself in brackets.

| Criterion | DI | Safety | FX Data | FX AC | FX CM |
|---|---:|---:|---:|---:|---:|
| Nodes present, with their node class | 413/447 (0) | 85/91 (0) | 65/186 (0) | 432/511 (0) | 414/545 (0) |
| BrowseName kept | 411/447 (0) | 85/91 (0) | 65/186 (0) | 432/511 (0) | 414/545 (0) |
| Description kept | 16/16 | 37/40 (3) | 1/7 (2) | 5/11 (2) | 0/4 (0) |
| ParentNodeId kept | 345/360 (0) | 73/74 (1) | 32/82 (0) | 378/437 (0) | 343/397 (0) |
| DataType kept | 231/246 (1) | 66/66 | 25/77 (0) | 272/311 (0) | 222/278 (0) |
| ValueRank and ArrayDimensions kept | 145/145 | 14/14 | 36/36 | 221/242 (0) | 272/272 |
| IsAbstract, Symmetric, InverseName kept | 28/28 | 2/2 | 7/7 | 20/20 | 23/23 |
| MethodDeclarationId kept | 28/28 | | 6/6 | 27/35 (0) | 14/14 |
| Values kept | 96/111 (1) | 12/13 (1) | 18/71 (1) | 73/108 (5) | 56/115 (3) |
| DataType definitions kept, per field | 29/36 (7) | 16/22 (6) | 113/125 (12) | 37/48 (11) | 230/244 (14) |
| References kept | 1448/1539 (1) | 220/236 (4) | 128/581 (0) | 1528/1826 (2) | 1433/1940 (16) |
| Documentation kept | 0/79 | 0/15 | 0/33 | 0/64 | 0/45 |

Every loss in brackets was traced back to the AML Opc2Aml writes:

- Definitions: the descriptions of option set bits; Annex A keeps none. Safety
  3005 and 3006 are option sets that Annex A writes without their fields.
- Descriptions: Annex A writes none for some DataTypes (Safety, FX).
- Values: empty strings, which Annex A leaves out, the EnumValues of variables
  (FX AC 1251, 1254), which Annex A writes empty, and structures of a model
  that is neither the exported one nor bundled (FX AC 6351 uses FX Data).
- References: a declaration's references to declarations of another type, for
  example to the placeholders of its own type definition (Safety 5000 to 6029),
  and references between declarations that Annex A does not write at all
  (FX CM 4001, HasCause 53).
- DataType: DI 468, a VariableType without a `Value` attribute.
- ParentNodeId: Safety 5002 is organized by Objects; DI writes no ParentNodeId
  for organized nodes, Safety does, and the AML cannot tell the two apart.
- Documentation: Annex A carries none.

The losses outside the brackets are nodes Annex A does not carry: the type
dictionaries and their variables (most of FX Data), the encodings, and nodes
no type or folder holds. Methods, DataTypes and ReferenceTypes, 0 % in chain
A, come back completely.

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
