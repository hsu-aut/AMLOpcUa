# AMLOpcUa

OPC UA information models in AutomationML.

- Plugin for the AutomationML Editor that imports OPC UA NodeSets as AML
  libraries according to OPC 10000-83 (UAFX Offline Engineering), Annex A
- Instances of UA types with their Mandatory children, chosen Optional
  children and named children for placeholders, and a check of instances
  against their types
- Running servers: browse, import the server's types (its published NodeSet,
  or rebuilt by browsing), take nodes into the document, bind, read and watch
  values, and serve the document itself as an OPC UA server
- Diagrams of UA types and instances (OPC 10000-3 notation, SVG export),
  UA Cloud Library import, instance updates after a type change, and links
  from a VDI 3682 process description to OPC UA objects and methods
- Export of AML documents (CAEX 2.15 and 3.0) as OPC UA NodeSets by the rules
  of the AutomationML/OPC Foundation working group (AML-UA-XSLT), a successor
  of OPC 30040, and, as a second mode that is not a standard, the inverse of
  Annex A: an imported OPC UA model written back as the nodes it was
- ModelDesign in and out: a model written in the form the OPC Foundation's
  ModelCompiler reads, and a design compiled and imported ([docs/modeldesign.md](docs/modeldesign.md))
- Command line tool `uaaml` with the same functions
- Structural comparison of AML class libraries and of NodeSets, used as test oracles

The conversion itself is done by [Opc2Aml](https://github.com/OPCF-Members/Opc2Aml),
the OPC Foundation's reference implementation of Annex A, included as source
with eight documented patches (see [third_party/Opc2Aml/UPSTREAM.md](third_party/Opc2Aml/UPSTREAM.md)).

The counterparts for the Formalised Process Description (VDI/VDE 3682) and for
Petri nets are [AMLFPB.js](https://github.com/hsu-aut/AMLFPB.js) and
AMLPetriNet.

## Repository

| Path | Content |
|---|---|
| `Aml.Editor.Plugin.OpcUa/` | AutomationML Editor plugin |
| `dotnet/OpcUaAml.Core/` | Library: NodeSet catalog, import, merge, export, comparison |
| `dotnet/OpcUaAml.Tool/` | Command line tool `uaaml` |
| `Aml.Editor.Plugin.OpcUa.Tests/` | Tests of the plugin's code that needs no editor (the safety net for exceptions) |
| `dotnet/OpcUaAml.Tests/` | Tests, including the comparison with the libraries the OPC Foundation publishes and with the unit tests of AML-UA-XSLT |
| `third_party/Opc2Aml/` | Opc2Aml source, with the patches in `third_party/patches/` |
| `nodesets/` | UA base model and DI, shipped with tool and plugin |
| `libraries/` | AutomationML object reference attribute types (embedded, for VDI 3682 links) |
| `docs/` | [import](docs/import.md), [instances and checks](docs/instances.md), [export](docs/export.md), [round trip](docs/roundtrip.md), [servers](docs/server.md), [diagram](docs/diagram.md), [modeler](docs/modeler.md), [ModelDesign](docs/modeldesign.md), [VDI 3682](docs/vdi3682.md), [demo](docs/demo.md) |

## Build

Requires the .NET 8 SDK. The plugin needs Windows and the AutomationML
Editor 6.4 or later, and bundles the graphical modeler: build
[NodeSet.js](../NodeSet.js) first (`npm install`, `npm run build`), it is
expected next to this repository.

```bash
dotnet build AMLOpcUa.sln
dotnet test dotnet/OpcUaAml.Tests
dotnet test dotnet/OpcUaAml.Tests --filter "Speed!=Slow"   # without servers and fresh conversions
dotnet test Aml.Editor.Plugin.OpcUa.Tests                   # the plugin's own code that needs no editor
```

Conversions are cached (see [import](docs/import.md)), so a second run of the
whole suite takes a few minutes; the fast run under half a minute. Some tests
read cases shared with NodeSet.js when it lies next to this repository.

The plugin package ends up in
`build/Plugins/Aml.Editor.Plugin.OpcUa/<configuration>/`.

## Plugin

Add the package folder as a source in the PlugIn Manager of the AutomationML
Editor and install the plugin from there. It opens as the tab "AMLOpcUa"; its first tab, "Namespaces", shows the OPC UA models of the document.

- **Import NodeSet** converts a `NodeSet2.xml` and adds its libraries to the
  open document: one `ATL_`, `ICL_`, `RCL_` and `SUC_` library per UA
  namespace, for the NodeSet and every model it requires, together with the
  `OpcAmlMetaModel` libraries.
- **Settings ▾ › NodeSet folders** lists folders searched for required
  NodeSets; **Forget fetched NodeSets** empties the folders of NodeSets taken
  from servers and the Cloud Library, which grow with every fetch. The folder of the imported NodeSet is always searched first. The
  UA base model and DI are built in. The same menu holds "Replace existing
  libraries", "Save after import" and the debug log. Saving goes through the
  editor's own save command, reached by reflection; when an editor version
  does not have it, the option is switched off with that reason.
- The list shows the UA namespaces the document holds, with model version and
  publication date. Beside it, the selected namespace in detail: its
  ObjectTypes, VariableTypes, DataTypes and ReferenceTypes, the namespaces it
  builds on and those built on it, the elements using its types, its types to
  draw, **Edit in the modeler**, **Documentation…** (one HTML page of the
  model: its types with their declarations and diagrams, its DataTypes and
  ReferenceTypes; `uaaml doc`), **Publish…** (the model's NodeSet to the UA
  Cloud Library, with title, description, copyright and license; the plugin
  asks once more before it sends; `uaaml cloud upload`), **ModelDesign…** (the
  model as a ModelDesign file with its identifier file, the form the OPC
  Foundation's ModelCompiler reads; [docs/modeldesign.md](docs/modeldesign.md)),
  and **Remove** when nothing needs it any more.
- **Companion specs…** lists the NodeSets the OPC Foundation publishes on
  GitHub (OPCFoundation/UA-Nodeset), every released companion specification,
  and imports one with the models it requires, without an account. The list
  comes from GitHub at most once a day; the UA Cloud Library, which needs an
  account or an API key, is one click away. On the command line: `uaaml opcf`.
- A ModelDesign file imported or dropped on the plugin is compiled into a
  NodeSet first, by the ModelCompiler where it is installed; without it, the
  status line says how to get it.
- NodeSet files dropped on the plugin are imported. When a NodeSet requires
  models no folder holds, a dialog names them and fetches them from the OPC
  Foundation's published NodeSets, or adds a folder, or searches the Cloud
  Library; the import then tries again. Models
  the plugin keeps itself (from the modeler, the Cloud Library, servers) are
  always searched. A document
  without OPC UA shows the first steps instead: import a NodeSet, search the
  Cloud Library, connect to a server, model a new information model.
- A second import of a namespace replaces its libraries in place; a newer model
  is never replaced by an older one.
- **New instance** creates an instance of a UA type: every Mandatory child,
  the Optional children ticked in the dialog, and for each placeholder the
  children named there, of its type or a concrete subtype.
- **Export** writes a NodeSet: the whole document by the AML-UA-XSLT rules,
  or one imported UA namespace back as the nodes it was (the inverse of
  Annex A, see [docs/export.md](docs/export.md)).
- **Check** lists findings on the "Check" tab: missing Mandatory children,
  unfilled MandatoryPlaceholders, abstract or unknown types, broken or
  mismatched reference links. A double click or Enter selects the element.
- The **Server** tab connects to a running server (recent endpoints are
  kept), imports its types, and takes the parts of its address space you
  check into the document; see [docs/server.md](docs/server.md). Its bands
  follow the tasks: the connection; what to do with the server (types, take
  into document, bind, values); what to take (depth, limit, target hierarchy,
  filters); and this document as a server of its own.
- The **Modeler** tab draws and edits OPC UA types and instances (OPC 10000-3
  Annex C) and applies the result to the document as a NodeSet import; see
  [docs/modeler.md](docs/modeler.md).

**Tutorial** (in the status bar, and a card on an empty document) opens four
short lessons in a panel beside the tabs: models into the document, instances
and checks, a running server (the document serves itself, so no plant is
needed), a model of your own. Each step frames the control it is about and
ticks itself off when the plugin's state shows it done; a button takes over
what needs a file or a place, such as opening the dialog in the folder of the
bundled DI. For those who know their way, a **?** beside each part says in a
few sentences what it does, explains its terms on a click and leads to the
lesson that shows it. A test checks that every lesson step and every **?**
names a control and a topic that exist.

Every tab has a toolbar in the style of the other plugins of this family
(icons of the editor's font, colour by kind of command); messages and
progress appear in one status bar below all tabs, with the whole message in
its tooltip and a link to the log that counts the warnings not seen yet. The
log shows each line's level in colour and opens its file. Dialogs share one frame: what the dialog
does in its header, the answer in its footer. Surfaces, lines and grey text are
mixed from the editor's theme (Aml.Skins on MahApps.Metro), so the plugin
reads in the light and the dark theme; the diagram stays a white sheet.

The plugin writes into the document directly: Ctrl+Z in the editor does not
take its changes back. Changes that touch many elements or remove some
(adding Mandatory children to every instance, removing a namespace or
elements a server no longer has, replacing a binding) show what they do and
ask first. An error in one of its commands is written to the log and shown
in the status bar; it does not reach the editor. Closed, the plugin ends its
session, stops serving and frees the port. It follows the editor's theme,
light or dark, also when it changes while the plugin is open.

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
uaaml instantiate plant.aml --type ConfigurableObjectType --name Modules --fill "<ObjectIdentifier>=ModuleA,ModuleB"
uaaml check   plant.aml
uaaml export  plant.aml -o plant.NodeSet2.xml
uaaml export  machinery.aml -o Machinery.NodeSet2.xml --annex-a-inverse
uaaml compare plant.NodeSet2.xml other.NodeSet2.xml
uaaml roundtrip Opc.Ua.Di.NodeSet2.xml plant.aml -o report.md
uaaml roundtrip Opc.Ua.Di.NodeSet2.xml --inverse -o report.md
uaaml browse  opc.tcp://plc:4840                 # secured; --insecure allows an unsecured server
uaaml serve   plant.aml --port 48400             # to this computer only
uaaml serve   plant.aml --network                # to other computers, trusted clients only
uaaml clients --trust 3F2A                       # admit a client the server refused
uaaml opcf search Machinery                      # the OPC Foundation's NodeSets, no account
uaaml opcf download http://opcfoundation.org/UA/Machinery/ -o ./nodesets
uaaml cloud search Machinery --user me           # password from UACLOUD_PASSWORD or asked
uaaml design export Opc.Ua.Di.NodeSet2.xml -o DI.xml    # the model as a ModelDesign
uaaml design export plant.aml -o Plant.xml              # the same, from a document
uaaml design import DI.xml --into plant.aml             # a design, compiled and imported (needs the ModelCompiler)
```

`info` exits with 1 when a required model cannot be found, `compare` when the
documents differ, `check` when there are errors, `roundtrip` when a chain
breaks off ([round trip](docs/roundtrip.md)). Given two NodeSets, `compare`
compares them as graphs.
