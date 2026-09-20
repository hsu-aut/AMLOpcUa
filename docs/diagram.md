# Diagram

The "Diagram" tab draws a UA type or an instance of one; **Export SVG** writes
the same picture as SVG. Picking an entry draws it (Enter in the search box
takes the first). A click on a shape selects its element in the editor, a
double click draws the shape's type (for a type, its supertype). **Fit** and
Ctrl + mouse wheel zoom.

## What is drawn

`DiagramBuilder` reads the Annex A representation back:

- the root: a type (a SystemUnitClass of an `SUC_` library, shown shaded with
  its supertype) or an instance (with its type after a colon);
- for a type, its instance declarations including inherited ones, as an
  instance of the type would contain them; for an instance, its children;
- per child: the node class from its type (a subtype of `BaseVariableType` is
  a Variable, an instance of `UaMethodNodeClass` a Method, anything else an
  Object), the ModellingRule (M, O, MP, OP, E) and the reference that attaches
  it, read from the child's interface class (`[HasComponent]/[ComponentOf]`
  belongs to HasComponent);
- non-hierarchical references (InternalLinks between elements that are not
  parent and child).

Shapes follow the node classes of OPC 10000-3: Object a rectangle, Variable a
rounded rectangle, Method an ellipse, ObjectType and VariableType the same
shapes shaded. References use the notation of OPC 10000-3 Annex C, as
NodeSet.js draws it (`EdgeGlyphs`): HasComponent one stroke across the line,
HasProperty two, HasTypeDefinition two filled heads, HasSubtype two hollow
heads at the supertype, other hierarchical references an open head,
non-hierarchical ones a filled head, symmetric ones a filled head at both
ends. All but HasComponent and HasProperty carry their name in italic.

The layout is a left-to-right tree: one column per level, leaves stacked,
parents centred on their children. Depth and node count are limited (default
3 levels, 300 nodes); a cut picture says so.

## State charts

A type that derives from FiniteStateMachineType gets a second picture, drawn
from what Annex A left in the document: the states on a circle, the
transitions as arrows between them with their name and the method that causes
them (`StateMachines`, `StateChart`). Two states usually have a transition
each way; each of them is drawn as a bow of its own, with its name at a
different point along it, because on one straight line the two arrows and
their names cover each other. It is the same drawing the modeler shows
while the machine is built, so both tell the same story, and it appears in the
documentation of a model beside the state and transition tables.

The ends of a transition are not hierarchical references; Annex A writes them
as ExternalInterfaces (FromState, ToState, HasCause) whose `ReferenceIds`
name the node they lead to, escaped as the element IDs are.

## Why WPF and not diagram-js

The plan was a WebView2 with diagram-js, as in the FPB and Petri net plugins.
For a picture that is only looked at, an editor framework brings a web build,
a bundle and a bridge without adding anything. The layout model lives in the
core, the view draws it with WPF shapes and the export writes it as SVG, so
both show the same thing and the export works without the editor. Editing the
diagram would be the point to add diagram-js on top of the same model.
