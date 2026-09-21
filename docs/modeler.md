# Modeler

The Modeler tab hosts the graphical modeler
[NodeSet.js](https://github.com/HamiedNabizada/NodeSet.js) in a WebView2 control. It
draws OPC UA types and instances in the notation of OPC 10000-3 Annex C and
edits NodeSet2 files: types, instance declarations with ModellingRules,
method arguments, structure and union fields, enumeration values and
OptionSets, values including structures of any shape, references,
instances by ModellingRule, checks, undo.

The modeler edits NodeSets, not the document. The document gets what the
modeler produces through the same import as every NodeSet (OPC 10000-83
Annex A), so the libraries it adds are the ones any other NodeSet would give.

| Action | What happens |
|---|---|
| **Edit** (a namespace of the document) | The NodeSet the namespace was imported from is looked up in the models folder and the NodeSet folders, and opened with every NodeSet it requires that the modeler does not ship (it ships UA and DI). |
| **New model** | An empty model with the namespace URI in the field. |
| **Open NodeSet file…** | Any NodeSet2 file; the NodeSets it requires come from its own folder and the NodeSet folders. |
| **Apply to document** (in the modeler) | The NodeSet is saved to `%LOCALAPPDATA%\AMLOpcUa\models\<namespace>.NodeSet2.xml` and imported into the document. The libraries of that namespace are replaced unless the document holds a newer publication. The modeler reports "Applying …" and counts the model as unchanged only once the plugin answers that the import worked. |
| **Save NodeSet** (in the modeler) | A save dialog of the plugin, not a browser download. |

The tab shows `Modeler *` while the modeler has changes that were neither
applied nor saved. Edit, New model, Open NodeSet file, Reload and "Open in
the modeler" from a server's namespaces ask before they drop such changes.
Inside the plugin the page hides its own New and Open: the plugin's resolve
the NodeSets a model requires from its folders. A draft in the node editor
(arguments, fields, a structure value) that was not applied is asked about
before another node is selected, and before Apply and Save. Diagram
positions are kept in the NodeSet's `Extensions` and survive the round trip
through the document.

## How it is built

- `Bridge/ModelerWebView.cs` hosts the page. The web build of NodeSet.js
  (`npm run build`, `dist/web`) ships in the plugin folder as
  `modeler-assets` and is served under the virtual host `nodeset.local`.
  WebView2 keeps its profile in `%LOCALAPPDATA%\AMLOpcUa\WebView2`.
- Messages are JSON. The plugin sends `open` (a NodeSet and the NodeSets it
  requires) or `new`; the page answers `ready` when it listens, `dirty`,
  `status`, `apply` with the NodeSet and `save` with the NodeSet and a file
  name. The plugin answers `apply` with `applied` and `save` with `saved`
  (`ok` and a text). A message sent before `ready` waits; a reload or a
  crashed renderer puts the bridge back into waiting.
- The control shows the modeler and nothing else: navigation to anything but
  `https://nodeset.local/` and new windows are refused, messages count only
  from that origin (whatever sends `apply` writes into the document), and the
  developer tools are off in a release build. A failed start of WebView2 is
  tried again when the tab is shown again.
- After `ready` the plugin sends `theme` with the editor's light or dark
  theme, again when the plugin becomes visible; the page's chrome follows,
  the canvas stays white. Standalone, the page follows the system setting.
- The build fails with a message when `dist/web` of NodeSet.js is missing.
  `WebView2Loader.dll` is staged under `obj/bundled` and packed next to the
  plugin's assemblies, because NuGet drops files from `runtimes/*/native`.

Checked with a WPF probe that loads the plugin, opens the tab, drives the page
through `ExecuteScriptAsync` (new model, a type with a variable, Apply) and
finds `SUC_http://example.org/Probe/` with `PumpType[Speed]` in the document.
The same probe applies a new model from the page and checks that the page
hears "applied" and is clean again, that the page hides New and Open, that the plugin asks
before New model drops changes, that a failed import reaches the page as a
warning with the model still marked changed, and that navigation away is
refused. The guards of the page on its own (asking before Open, before
leaving a draft, before leaving the page) were checked in Edge with
Playwright. Not yet tried inside the AutomationML Editor itself.
