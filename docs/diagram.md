# Diagram

The "Diagram" tab draws a UA type or an instance of one; **Export SVG** writes
the same picture as SVG.

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
  parent and child) as dashed arrows.

Shapes follow the node classes of OPC 10000-3: Object a rectangle, Variable a
rounded rectangle, Method an ellipse, ObjectType and VariableType the same
shapes shaded. References are arrows labelled with their type rather than the
reference symbols of OPC 10000-3, which are hard to tell apart at small sizes.

The layout is a left-to-right tree: one column per level, leaves stacked,
parents centred on their children. Depth and node count are limited (default
3 levels, 300 nodes); a cut picture says so.

## Why WPF and not diagram-js

The plan was a WebView2 with diagram-js, as in the FPB and Petri net plugins.
For a picture that is only looked at, an editor framework brings a web build,
a bundle and a bridge without adding anything. The layout model lives in the
core, the view draws it with WPF shapes and the export writes it as SVG, so
both show the same thing and the export works without the editor. Editing the
diagram would be the point to add diagram-js on top of the same model.
