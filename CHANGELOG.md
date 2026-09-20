# Changelog

The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and the versions follow [Semantic Versioning](https://semver.org/spec/v2.0.0.html).
Until 1.0.0 the command line and the plugin's API may still change between
minor versions.

## [Unreleased]

### Added

- A NodeSet can be served as an OPC UA server: `uaaml serve --nodeset
  <file>`. It serves the model with its types, loads the models it requires
  first, and offers each of them as the NamespaceFile of its namespace
  metadata, so a client takes the NodeSet from the server instead of a file.
  With `--simulate` the values move. `examples/mps500` is such a plant, the
  MPS 500 learning factory on DI, with stations, modules, devices and a state
  machine per station (`docs/server.md`).
- `uaaml read <endpoint> <node>...` prints the current values of nodes. A node
  is a NodeId, or a path of BrowseNames from the Objects folder
  (`/MPS500/ST30_Processing/ParameterSet/Ready`), so a value can be read
  without knowing a server's NodeIds.
- Finite state machines are read back out of a document (OPC 10000-5 Annex B,
  as Annex A leaves them: states and transitions of StateType and
  TransitionType, the ends as ExternalInterfaces) and the documentation of a
  model shows them as a table and as a state chart, the same picture the
  modeler draws.
- `uaaml design export` also reads an AML document: its model is written back
  as the nodes Annex A made of it, and that becomes the design.

### Fixed

- A state chart drew the way there and the way back as one line, so one arrow
  and one name covered the other. Every transition of a pair now gets a bow
  and a place of its own, in the plugin and in the modeler
  (`docs/diagram.md`).
- A state machine of a type whose name carries an underscore kept its states
  and transitions but none of their ends: the path of a declaration was joined
  with `_` and split on `_` again (Opc2Aml patch 0009). Measured on Machinery,
  where one machine had 16 transitions and no ends while its neighbour had 16
  of each; 26 NodeSets of the OPC Foundation carry such names.
- An import says what it leaves behind: the libraries are taken, the model's
  instances are not, and a model that is nothing but instances (a dictionary,
  for one) used to report success and arrive empty. It also names the children
  that hang off their type only through `Organizes` or a reference type the
  model defines itself, which Annex A does not map: they and their subtree are
  missing, and nothing used to say so (`docs/roundtrip.md`).
- The inverse export no longer hands out NodeIds that the original model uses
  for other nodes. Annex A drops the encodings and the type dictionaries, and
  the invented ids started exactly where those had been; they now start at a
  round distance above everything the model uses. An abstract structure gets
  no encodings, and no JSON encoding is invented for a model that declared
  none.
- A state machine is recognised by the NodeIds of the base types, not by their
  names, so a model with a type of its own called StateType is not mistaken
  for one; and the method that causes a transition is found wherever the model
  puts it.
- An instance keeps what an overridden declaration holds. AML replaces a
  declaration with everything below it, OPC UA replaces the node and keeps the
  hierarchy below it, so an instance of `3DFrameType` used to lose the
  `LengthUnit` that `FrameType` declares under `CartesianCoordinates`. Of 422
  overriding declarations in the released companion specifications, 191 have
  children of their own (`docs/instances.md`).
- A declaration with ExposesItsArray no longer produces exactly one child under
  a name the specification does not give it; the modeler and the plugin now
  agree (`docs/instances.md`).
- Two declarations whose BrowseNames differ only in their namespace are two
  declarations again; keyed by the bare name, one of them was dropped and the
  generated library kept a link to an element nobody wrote.
- The export writes a NodeSet the OPC Foundation's stack can read: no
  reference into an empty namespace (a supertype from a library the document
  does not hold), no alias name standing for two nodes, no library or model
  declared twice, no reference carried twice, and a class is looked for in a
  library of the matching kind (deviations D18 and D19 in `docs/export.md`).
  Found by an audit that ran the working group's stylesheet against 30
  documents and loaded every result with the stack.
- A placeholder counts as filled whatever the child is called: a method keeps
  the name its declaration gives it, so the check used to report the correct
  model and stay silent on the wrong one, and the filling refused its own
  name.
- The ModelDesign writer gives every node a symbolic path of its own (two
  BrowseNames that turned into one symbol used to share it, which made the
  identifier file map one key to two ids), keeps nodes that no parent declares
  forward instead of dropping them while references still pointed at them,
  leaves a child that two parents hold in one of them and as a reference in
  the other, names a reference target by its symbolic path, spells a matrix
  rank the way a design can, carries a string NodeId on the node, and cannot
  break the identifier file with a comma in a name.
- Compiling twice into the same folder reports the NodeSet of this run, not an
  older one beside it.
- A ModelDesign dropped on the plugin is compiled, as the documentation said
  and the file dialog promised.

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
