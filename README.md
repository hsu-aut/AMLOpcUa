# AMLOpcUa

OPC UA information models in AutomationML.

- Plugin for the AutomationML Editor that imports OPC UA NodeSets as AML
  libraries according to OPC 10000-83 (UAFX Offline Engineering), Annex A
- Instances of UA types with their Mandatory children and chosen Optional
  children, and a check of instances against their types
- Command line tool `uaaml` with the same functions
- Structural comparison of AML class libraries, used as the test oracle

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
| `dotnet/OpcUaAml.Core/` | Library: NodeSet catalog, import, merge, comparison |
| `dotnet/OpcUaAml.Tool/` | Command line tool `uaaml` |
| `dotnet/OpcUaAml.Tests/` | Tests, including the comparison with the libraries the OPC Foundation publishes |
| `third_party/Opc2Aml/` | Opc2Aml source, with the patches in `third_party/patches/` |
| `nodesets/` | UA base model and DI, shipped with tool and plugin |
| `docs/` | Import ([import.md](docs/import.md)), instances and checks ([instances.md](docs/instances.md)) |

## Build

Requires the .NET 8 SDK. The plugin needs Windows and the AutomationML
Editor 6.4 or later.

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
- **NodeSet folders** lists folders searched for required NodeSets. The folder
  of the imported NodeSet is always searched first. The UA base model and DI
  are built in.
- The list shows the UA namespaces the document holds, with model version and
  publication date.
- A second import of a namespace replaces its libraries in place; a newer model
  is never replaced by an older one.
- **New instance** creates an instance of a UA type: every Mandatory child,
  the Optional children ticked in the dialog, no placeholders.
- **Check** lists findings on the "Check" tab: missing Mandatory children,
  unfilled MandatoryPlaceholders, abstract or unknown types, broken or
  mismatched reference links.

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
```

`info` exits with 1 when a required model cannot be found, `compare` when the
documents differ, `check` when there are errors.
