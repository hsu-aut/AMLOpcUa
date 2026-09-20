# Changelog

The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and the versions follow [Semantic Versioning](https://semver.org/spec/v2.0.0.html).
Until 1.0.0 the command line and the plugin's API may still change between
minor versions.

## [Unreleased]

Nothing yet.

## [0.1.0] - 2026-09-20

The first release: OPC UA information models in AutomationML, as a plugin for
the AutomationML Editor and as the command line tool `uaaml`.

### Added

- **Import** of OPC UA NodeSets as AML libraries by OPC 10000-83 (UAFX Offline
  Engineering) Annex A, using the OPC Foundation's own Opc2Aml, vendored with
  eight documented patches (`third_party/Opc2Aml/UPSTREAM.md`). Conversions are
  cached, so a second run of the same file takes 0.7 s instead of 15 s.
- **Instances** of UA types with their Mandatory children, chosen Optional
  children and named children for placeholders, and a check of instances
  against their types.
- **Export** of AML documents (CAEX 2.15 and 3.0) as NodeSets by the rules of
  the AutomationML/OPC Foundation working group (AML-UA-XSLT), with 17
  documented deviations (`docs/export.md`), and, as a second mode that is not a
  standard, the inverse of Annex A.
- **Round trip** measurements of both directions (`uaaml roundtrip`): over 34
  companion specifications, 94.9 % of the facts come back and 0.17 % are lost
  on the way back; the rest is Annex A (`docs/roundtrip.md`).
- **Servers**: browse, take the server's types into the document (its published
  NodeSet or rebuilt by browsing), mirror nodes with their values, bind
  elements, read and watch values, and serve the document itself as an OPC UA
  server, local by default, secured and with trusted clients on the network,
  optionally with simulated values.
- **Diagrams** of UA types and instances in the notation of OPC 10000-3, in the
  plugin and as SVG.
- **Sources of models**: the NodeSets the OPC Foundation publishes on GitHub
  (no account), the UA Cloud Library (search, download, and publishing a model
  of the document), and NodeSet files dropped on the plugin.
- **Documentation** of a model as one self-contained HTML page.
- **ModelDesign** in and out (`docs/modeldesign.md`): a model written in the
  form the OPC Foundation's ModelCompiler reads, with the identifier file that
  keeps the NodeIds, and a design compiled and imported in one go. 417 of DI's
  447 own nodes come back identical through that round trip.
- **Links** from a VDI 3682 process description to OPC UA objects and methods.
- **The graphical modeler** [NodeSet.js](../NodeSet.js) in the Modeler tab,
  which opens a namespace of the document and imports the result back.
- **A guided tutorial** in the plugin, four lessons that watch the plugin's own
  state, and a "?" beside every part of the window for those who know OPC UA
  already.
- **Command line tool** `uaaml` with the same functions, so everything can be
  scripted and tested without the editor.

### Known limits

- Not tested inside the AutomationML Editor itself; everything is driven
  through a probe that hosts the plugin in a window of its own.
- The modeler is bundled from `NodeSet.js/dist/web`; a build without it
  (`-p:SkipModeler=true`) produces a plugin whose Modeler tab has nothing to
  show, which is what CI checks.
- The document must be CAEX 3.0 (AutomationML 2.10); a 2.15 document is
  converted on import.
