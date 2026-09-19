# AMLOpcUa

OPC UA information models in AutomationML.

- Plugin for the AutomationML Editor that imports OPC UA NodeSets as AML
  libraries according to OPC 10000-83 (UAFX Offline Engineering), Annex A
- Instances of UA types with their Mandatory children and chosen Optional
  children, and a check of instances against their types
- Running servers: browse, import the server's types (its published NodeSet,
  or rebuilt by browsing), take nodes into the document, bind, read and watch
  values, and serve the document itself as an OPC UA server
- Diagrams of UA types and instances (OPC 10000-3 notation, SVG export),
  UA Cloud Library import, instance updates after a type change, and links
  from a VDI 3682 process description to OPC UA objects and methods
- Export of AML documents (CAEX 2.15 and 3.0) as OPC UA NodeSets by the rules
  of the AutomationML/OPC Foundation working group (AML-UA-XSLT), a successor
  of OPC 30040
- Command line tool `uaaml` with the same functions
- Structural comparison of AML class libraries and of NodeSets, used as test oracles

The conversion itself is done by [Opc2Aml](https://github.com/OPCF-Members/Opc2Aml),
the OPC Foundation's reference implementation of Annex A, included as source
with two documented patches (see [third_party/Opc2Aml/UPSTREAM.md](third_party/Opc2Aml/UPSTREAM.md)).

The counterparts for the Formalised Process Description (VDI/VDE 3682) and for
Petri nets are [AMLFPB.js](https://github.com/hsu-aut/AMLFPB.js) and
AMLPetriNet.

## Repository

| Path | Content |
|---|---|
| `Aml.Editor.Plugin.OpcUa/` | AutomationML Editor plugin |
| `dotnet/OpcUaAml.Core/` | Library: NodeSet catalog, import, merge, export, comparison |
| `dotnet/OpcUaAml.Tool/` | Command line tool `uaaml` |
| `dotnet/OpcUaAml.Tests/` | Tests, including the comparison with the libraries the OPC Foundation publishes and with the unit tests of AML-UA-XSLT |
| `third_party/Opc2Aml/` | Opc2Aml source, with the patches in `third_party/patches/` |
| `nodesets/` | UA base model and DI, shipped with tool and plugin |
| `libraries/` | AutomationML object reference attribute types (embedded, for VDI 3682 links) |
| `docs/` | [import](docs/import.md), [instances and checks](docs/instances.md), [export](docs/export.md), [round trip](docs/roundtrip.md), [servers](docs/server.md), [diagram](docs/diagram.md), [modeler](docs/modeler.md), [VDI 3682](docs/vdi3682.md) |

## Build

Requires the .NET 8 SDK. The plugin needs Windows and the AutomationML
Editor 6.4 or later, and bundles the graphical modeler: build
[UaModeler.js](../UaModeler.js) first (`npm install`, `npm run build`), it is
expected next to this repository.

```bash
dotnet build AMLOpcUa.sln
dotnet test dotnet/OpcUaAml.Tests
```

The plugin package ends up in
`build/Plugins/Aml.Editor.Plugin.OpcUa/<configuration>/`.

## Plugin

Add the package folder as a source in the PlugIn Manager of the AutomationML
Editor and install the plugin from there. It opens as the tab "AMLOpcUa".

- **Import NodeSet** converts a `NodeSet2.xml` and adds its libraries to the
  open document: one `ATL_`, `ICL_`, `RCL_` and `SUC_` library per UA
  namespace, for the NodeSet and every model it requires, together with the
  `OpcAmlMetaModel` libraries.
- **Settings ▾ › NodeSet folders** lists folders searched for required
  NodeSets. The folder of the imported NodeSet is always searched first. The
  UA base model and DI are built in. The same menu holds "Replace existing
  libraries", "Save after import" and the debug log.
- The list shows the UA namespaces the document holds, with model version and
  publication date; a double click opens one in the modeler. A document
  without OPC UA shows the first steps instead: import a NodeSet, search the
  Cloud Library, connect to a server, model a new information model.
- A second import of a namespace replaces its libraries in place; a newer model
  is never replaced by an older one.
- **New instance** creates an instance of a UA type: every Mandatory child,
  the Optional children ticked in the dialog, no placeholders.
- **Check** lists findings on the "Check" tab: missing Mandatory children,
  unfilled MandatoryPlaceholders, abstract or unknown types, broken or
  mismatched reference links. A double click selects the element.
- The **Server** tab connects to a running server (recent endpoints are
  kept), imports its types, and takes the parts of its address space you
  check into the document; see [docs/server.md](docs/server.md).
- The **Modeler** tab draws and edits OPC UA types and instances (OPC 10000-3
  Annex C) and applies the result to the document as a NodeSet import; see
  [docs/modeler.md](docs/modeler.md).

Every tab has a toolbar in the style of the other plugins of this family
(icons of the editor's font, colour by kind of command); messages and
progress appear in one status bar below all tabs, the log shows each line's
level in colour and opens its file. Dialogs share one frame: what the dialog
does in its header, the answer in its footer.

The document must be CAEX 3.0 (AutomationML 2.10). Do not copy
`Aml.Editor.Plugin.Contract.dll` into an installed plugin folder; the editor
ignores the plugin without an error message if it finds a second copy.

## Command line

```bash
uaaml info    Opc.Ua.Machinery.NodeSet2.xml --search ./nodesets
uaaml import  Opc.Ua.Machinery.NodeSet2.xml --search ./nodesets -o machinery.aml
uaaml import  Opc.Ua.Machinery.NodeSet2.xml --into plant.aml
uaaml compare machinery.aml Opc.Ua.Machinery.NodeSet2.xml.amlx --skeleton
uaaml types   plant.aml --filter Machine
uaaml instantiate plant.aml --type SoftwareVersionType --name Firmware --optional ReleaseDate
uaaml check   plant.aml
uaaml export  plant.aml -o plant.NodeSet2.xml
uaaml compare plant.NodeSet2.xml other.NodeSet2.xml
uaaml roundtrip Opc.Ua.Di.NodeSet2.xml plant.aml -o report.md
```

`info` exits with 1 when a required model cannot be found, `compare` when the
documents differ, `check` when there are errors, `roundtrip` when a chain
breaks off ([round trip](docs/roundtrip.md)). Given two NodeSets, `compare`
compares them as graphs.
