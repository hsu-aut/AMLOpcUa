# Modeler

The Modeler tab hosts the graphical modeler
[UaModeler.js](../../UaModeler.js) (working name) in a WebView2 control. It
draws OPC UA types and instances in the notation of OPC 10000-3 Annex C and
edits NodeSet2 files: types, instance declarations with ModellingRules,
method arguments, structure fields and enumeration values, references,
instances by ModellingRule, checks, undo.

The modeler edits NodeSets, not the document. The document gets what the
modeler produces through the same import as every NodeSet (OPC 10000-83
Annex A), so the libraries it adds are the ones any other NodeSet would give.

| Action | What happens |
|---|---|
| **Edit** (a namespace of the document) | The NodeSet the namespace was imported from is looked up in the models folder and the NodeSet folders, and opened with every NodeSet it requires that the modeler does not ship (it ships UA and DI). |
| **New model** | An empty model with the namespace URI in the field. |
| **Open NodeSet file…** | Any NodeSet2 file; the NodeSets it requires come from its own folder and the NodeSet folders. |
| **Apply to document** (in the modeler) | The NodeSet is saved to `%LOCALAPPDATA%\AMLOpcUa\models\<namespace>.NodeSet2.xml` and imported into the document. The libraries of that namespace are replaced unless the document holds a newer publication. |

The tab shows `Modeler *` while the modeler has changes that were neither
applied nor saved. Diagram positions are kept in the NodeSet's `Extensions`
and survive the round trip through the document.

## How it is built

- `Bridge/ModelerWebView.cs` hosts the page. The web build of UaModeler.js
  (`npm run build`, `dist/web`) ships in the plugin folder as
  `uamodeler-assets` and is served under the virtual host `uamodeler.local`.
  WebView2 keeps its profile in `%LOCALAPPDATA%\AMLOpcUa\WebView2`.
- Messages are JSON. The plugin sends `open` (a NodeSet and the NodeSets it
  requires) or `new`; the page answers `ready` when it listens, `dirty`,
  `status`, and `apply` with the NodeSet. A message sent before `ready` waits;
  a reload or a crashed renderer puts the bridge back into waiting.
- The build fails with a message when `dist/web` of UaModeler.js is missing.
  `WebView2Loader.dll` is staged under `obj/bundled` and packed next to the
  plugin's assemblies, because NuGet drops files from `runtimes/*/native`.

Checked with a WPF probe that loads the plugin, opens the tab, drives the page
through `ExecuteScriptAsync` (new model, a type with a variable, Apply) and
finds `SUC_http://example.org/Probe/` with `PumpType[Speed]` in the document.
Not yet tried inside the AutomationML Editor itself.
