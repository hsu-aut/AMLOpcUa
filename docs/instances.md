# Instances and checks

## Creating an instance

`TypeInstantiator` (command line: `uaaml instantiate`, plugin: **New instance**)
creates an instance of a UA type the way an OPC UA server creates one:

| ModellingRule of a child declaration | In the instance |
|---|---|
| Mandatory, ExposesItsArray | always |
| Optional | only if chosen (by path relative to the instance, e.g. `Identification/Manufacturer`) |
| MandatoryPlaceholder, OptionalPlaceholder (`<Name>`) | the concrete children named for it (`--fill`, or in the dialog), else none |
| none (not an instance declaration, such as DefaultInstanceBrowseName) | never |

A placeholder's child is a copy of the placeholder's declaration, which Annex
A writes with the whole structure of its type, renamed and linked to its
owner the way the placeholder was; its own children follow the same rules.
Given another type (a subtype of the placeholder's, for instance a concrete
one for an abstract `<DeviceName>`), the child is an instance of that type and
gets the placeholder's end of the link. The dialog offers the declared type
and its concrete subtypes, and preselects a concrete one when the declared
type is abstract.

```bash
uaaml instantiate plant.aml --type ConfigurableObjectType --name Modules     --fill "<ObjectIdentifier>=ModuleA,Firmware:SoftwareVersionType"
```

Annex A puts a child's rule on the child's end of the reference that attaches
it to its parent (for instance the `ComponentOf` interface), which is where it
is read from.

Aml.Engine's `CreateClassInstance` flattens the type hierarchy (a declaration in
a subtype overrides the one of the same name in the supertype), assigns new IDs
and rewires the InternalLinks. After pruning, links into removed children are
deleted as well.

AML and OPC UA mean different things by that override. AML replaces the
declaration with everything below it; OPC UA replaces the node and keeps the
hierarchy below it (OPC 10000-3, the fully inherited instance declaration
hierarchy). `3DFrameType` declares `CartesianCoordinates` of a narrower type
than `FrameType` does, and a server still gives it the `LengthUnit` that
`FrameType` declares below it. The instantiation therefore walks the type chain
itself: for every declaration that overrides another, the children the
overridden one holds and the new one does not are copied in, with the reference
that holds them, and the same is done further down. A copied child then follows
the rules of the table above like any other, so an inherited Optional child
stays optional. Measured against the released companion specifications, 191 of
422 overriding declarations have children of their own, so this is not a corner
case.

What only makes sense on a type is removed from the instance:

- `IsAbstract` (Annex A, Table A.6: "In instances, this attribute has no meaning")
- the root's `BrowseName`: its namespace is the type's, while the instance
  belongs to the namespace of the server it is deployed to; without it Annex A
  infers the name from the element
- every `NodeId`: the copied values identify the type's instance declarations,
  not nodes of the instance (keep them with `KeepNodeIds`)
- `ModellingRule` on the interfaces (Annex A: present "within an SUC")

Abstract types are refused unless explicitly allowed.

In the dialog an Optional child below another Optional child can be ticked
only once its parent is. Before creating, the dialog says once when a
MandatoryPlaceholder is left empty or the hierarchy already holds an element
of that name; Create again goes ahead. Enter in the type search takes the
first type found and moves on to the name. The new instance is selected in
the editor.

**Add missing Mandatory children** (`InstanceUpgrader`) brings instances up
to date after a newer version of their types was imported. The plugin lists
what it would add (`PreviewDocument`) and asks before it adds anything.

## Checking a document

`AnnexAChecker` (command line: `uaaml check`, plugin: **Check**) checks the
instance hierarchies; the generated libraries are trusted.

| Rule | Severity | Finding |
|---|---|---|
| UA001 | Error | The element refers to a UA type (`[SUC_…]` path) the document does not contain. |
| UA002 | Error | The element instantiates an abstract type. |
| UA003 | Error | A Mandatory child of the type (including inherited ones) is missing, by name. |
| UA004 | Error | A MandatoryPlaceholder has no child of its type or a subtype. |
| UA005 | Error | An InternalLink side does not resolve. |
| UA006 | Error | An InternalLink connects reference interfaces against `RefClassConnectsToPath` (Annex A, Table A.8), inherited along the interface class chain. |
| UA007 | Warning | An element named like a placeholder (`<Name>`) sits in an instance hierarchy. |

## Why not OCL.NET (yet)

The plan was to express these rules in OCL with
[OCL.NET](https://github.com/HamiedNabizada/OCL.NET). Two things stand in the
way for now:

- The CAEX binding (`OCL.NET.Caex`) navigates instances (children, attributes,
  connections) and is shaped around its first domain (`process`, `bounds`).
  UA003, UA004 and UA006 need the step from an element to its type and the
  type's inherited declarations, which the binding does not offer. A
  `UaTypeRegistry` alone is not enough; the binding needs a navigation to the
  type.
- OCL.NET is not published as a package, so this repository would have to
  reference it by a local path.

The rules are therefore C# for now, with fixed IDs so that an OCL version can
replace them rule by rule and be tested against the same cases
(`InstanceTests`).

### ExposesItsArray

A declaration with this rule says that an instance exposes one child per
element of its array, and the specification leaves the BrowseNames of those
children open (OPC 10000-3 6.4.4.4.3). How many there are is known where the
values are, not where the instance is created, so a new instance gets none of
them rather than exactly one under a name nobody chose. The modeler does the
same.
