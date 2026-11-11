# Changelog

The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and the versions follow [Semantic Versioning](https://semver.org/spec/v2.0.0.html).
Until 1.0.0 the command line and the plugin's API may still change between
minor versions.

## [Unreleased]

### Added

- **Practice plant** beside Connect in the Server tab: starts the MPS 500
  learning factory, which the plugin now brings, as an OPC UA server on this
  computer with simulated values, and puts its address in the endpoint box.
  Connecting, taking types and nodes and keeping values live can be tried
  without a plant and without the command line. It takes the next free port
  when another server holds 4840.

### Fixed

- Cancel in "Trust this server?" was logged and told as an error; it says
  "Not connected" now. A server that refuses this computer's certificate is
  named as such, with the folder of the certificate its administrator has to
  trust, instead of asking to trust the server again.
- Importing a NodeSet that declares the same alias twice failed after its
  libraries were already in the document; the import goes through and uses
  the last value, as Opc2Aml does. The round trip and a server's NodeSet read
  such files too.
- Filling a placeholder with a chosen subtype left out children the subtype
  inherits below a declaration it overrides (for example the state counters
  below Counters of the PubSub diagnostics types); they are there now, as in a
  new instance of that type.
- The check, "Add missing Mandatory children" and the diagram looked at a
  type's top-level declarations only, so children required below a child were
  neither checked, added nor drawn; they are now. **Existing documents may show
  new UA003 or UA004 findings**, for example a DI device whose ParameterSet is
  present but holds no parameter: fill one, or leave ParameterSet out.
- One NodeSet file in a NodeSet folder that could not be read (held by another
  program, no read rights) made the whole import fail; it is skipped with a
  warning.
- NodeSets without a Models element (valid before OPC UA 1.04) were refused
  and never counted as a required model; they stand for their first namespace
  now, as Opc2Aml reads them.
- Removing a namespace ignored interfaces, attributes and roles in instance
  hierarchies and the user's own libraries that pointed into it, and left
  their paths dangling; such uses keep the namespace and are named as reasons.
- The OPC Foundation's list of companion specifications downloaded every file
  again and failed as a whole when one file could not be read. Unchanged files
  are reused, a failing file keeps its entry from before, and without a
  connection the list and files from before are used, which the dialog and the
  log say.
- When fetching missing models from the OPC Foundation brought none (offline,
  or a model it does not publish), the import ended with "cancelled: required
  models are missing" although nobody cancelled, and the reason was gone. The
  dialog of missing models comes back with the reason, so a folder or the
  Cloud Library can be tried.
- "Edit in the modeler" found a namespace's NodeSet only in the NodeSet
  folders; one imported from anywhere else ended in "No NodeSet of … found".
  The plugin keeps a copy of every NodeSet imported from another folder
  (`%LOCALAPPDATA%\AMLOpcUa\imported`), and later imports find it there too.
  On the UA base model the command says that the modeler has it built in,
  instead of opening an empty tab.
- "Remove server addresses…" saved an .amlx document as a plain .aml file,
  without the libraries, geometry and other parts of the container; the copy of
  a container is a container again, with every other part as it was.
- "Types of the server…" with "Include the namespace's objects" ticked and
  Import types browsed the objects for nothing and reported them as not taken;
  an import fetches the types only.
- "Check again" and "Add missing Mandatory children" were enabled without a
  document, and "Export SVG…" without a diagram; a click did nothing. They are
  greyed out until they can work.
- The address space showed its top nodes twice when it was loaded twice at
  once (two filter boxes clicked quickly, or F5 twice).
- A node that could not be expanded, because the server did not answer, stayed
  empty for good and nothing was said; it folds again with the reason in the
  status, and expanding it tries again.
- Disconnecting from a server that is gone showed nothing while the
  subscriptions ended, and Disconnect could be clicked again; the plugin is
  busy now until it is done.
- The endpoint offered first is `opc.tcp://127.0.0.1:4840`: with `localhost`
  every connection to a server on 127.0.0.1 waited about two seconds for ::1.
- One error showed at the log link as many new warnings, one per line of its
  stack trace; it counts once now, and the link says "problem(s)".
- A failed "Add missing Mandatory children" was reported half in English in
  the German interface.
- The Secure box and its help said "signed and encrypted only"; it takes the
  most secure endpoint the server offers, which may be signed only. The texts
  say so now.

- "Take into document" that failed (the server had stopped) left an empty
  InstanceHierarchy behind; one made for it goes again.
- While the document was served, what "Take into document" or "New
  instance…" reported was replaced about 0.3 s later by the hint to restart
  serving; the hint is added to it now.
- A modeler that could not start (WebView2 missing, the modeler's files
  missing) never tried again until the editor was restarted, and Edit then
  said "Opened …" over the reason although nothing was shown. Each visit to
  the tab tries again, and nothing is reported as opened while it does not
  run. Saving from the modeler no longer opens its dialog inside WebView2's
  message event, which WebView2 does not allow.
- Keys pressed in the Modeler tab reached the editor first: Ctrl+Z undid the
  last change of the AML document instead of the modeler's, and Ctrl+S, Ctrl+C,
  Ctrl+V and Delete acted on the document too. The modeler now keeps its keys.

- Importing failed on a computer with .NET 10 and no .NET 8, as one set up
  for the AutomationML Editor 6.4.3.8 can be: the converter did not start.
  It now runs on the next newer .NET, and so does `uaaml`.
- A selected line of the log shows its time and level in the colour of the
  selection; in grey and blue they could hardly be read on it.
- The plugin is built against Aml.Engine 4.5.2, the version it was tested
  with, and asks the editor for that or newer. It took the newest 4.x on
  nuget.org at build time and then needed an editor at least as new.
- "Types of the server…" uses the models fetched before from the OPC
  Foundation, the Cloud Library or other servers. It fetched them again from
  the server, often rebuilt by browsing and with less in them.

## [0.2.0] - 2026-11-09

### Added

- The Modeler tab brings NodeSet.js 0.2.0: German interface, bounded undo,
  two-dimensional matrices, sub-state machines, structure fields that allow
  subtypes, and a user manual.
- A user manual, `docs/manual.md`, with pictures taken in the AutomationML
  Editor.
- The plugin speaks German when the display language of Windows is German,
  and hands the language to the embedded modeler (NodeSet.js), which speaks it
  too. `AMLOPCUA_LANGUAGE=en` or `de` overrides it. Messages that come verbatim from the OPC UA stack, the conversion or
  the core library stay English.
- **Remove server addresses…** (Server tab) and `uaaml strip-addresses` save a
  copy of a document without where its servers are reached (endpoints, server
  URIs, the ServerInstanceUri of every NodeId, EndpointURL, DiscoveryURL), to
  pass it on, and name every place a host name still appears
  (`docs/server.md`).
- Instances drawn in the modeler reach the document: after Apply a dialog asks
  where they go, into an InstanceHierarchy of their own (replaced on every
  Apply), below the element selected in the editor (with IDs of their own), or
  not at all. Before, only the types were taken (`docs/modeler.md`).
- The plugin logs how long each part of an import takes: conversion, merge,
  the editor updating its trees, saving.
- State charts and the documentation show a state that holds a machine of its
  own (HasSubStateMachine): a second outline and the machine's type below it,
  and a column in the state table, as the modeler draws it
  (`docs/diagram.md`).

### Fixed

- An import no longer adds a second, older copy of the AutomationML base
  libraries to a document that references them through an ExternalReference
  (as the AutomationML Editor does, in 2.11.0): the imported classes point at
  the referenced library instead, and the editor no longer reports libraries
  that cannot be verified. The copy is still added when the referenced file
  lacks a class the import needs, and the report says which (`docs/import.md`).
- Opening or closing a document in the AML Editor no longer raises "The PlugIn
  AMLOpcUa raised an exception!". The editor unloads a document before it
  tells the plugin, and the plugin failed on the unloaded one; it then kept
  working on the closed document instead of the new one.
- An import in the AML Editor takes about as long as on the command line
  instead of freezing the editor for half an hour and more. The plugin now
  converts in a process of its own, with the converter it brings
  (`converter\uaaml.exe convert`, a new command). Inside the editor,
  Aml.Engine recorded every change of the converter's documents in the
  editor's undo history (1.5 million for a small model), which the editor
  answered on its UI thread, and the editor's unique name service rewrote
  references on every inserted element and could rename what Annex A names
  on purpose. A crash of the converter no longer ends the editor. Opc2Aml
  patch 0011 keeps the converter's documents out of a registered undo service
  in any host. The import also logs when it starts, and its progress text is
  translated (`docs/import.md`).
- Importing a NodeSet again into a document in the AML Editor no longer
  takes the types from the instances of it. The editor keeps automatic
  reference updates on, and removing the old library before the new one
  arrived cleared every reference into it. The libraries now change hands
  with those updates held, and the merge takes seconds instead of minutes
  (UA and DI: 3 s instead of 59 s). Removing a namespace and replacing the
  modeler's instance hierarchy work the same way. The core now builds
  against Aml.Engine.Services 4.3.1, the version the editor brings
  (`docs/import.md`).
- **Remove server addresses…** sits in the band "This document as a server".
  At the end of the tool bar above it went into the overflow first, even
  with the editor at full width. The band wraps in a narrow pane, as beside
  the editor's trees, instead of cutting off its last commands.
- The endpoint URL on the Server tab keeps room to be typed in a narrow pane:
  user, password and Secure then go to a second line. Beside the editor's
  trees the box had no width at all. The connection state shows in full as
  a tooltip, and Tab moves through the band from left to right.
- Every button of the plugin has a name for screen readers and UI
  Automation: the text it shows, else its tooltip. A button of glyph and
  text had none. So do the entries of the plugin's dialog lists (the
  companion specifications, the Cloud Library, the missing models), the
  nodes of a server's address space and their check boxes; they were read
  out as "System.Windows.Controls.ListBoxItem" and the like.
- The grey detail of a list entry (namespace, version, date) and the scope
  beside a node of a server's address space ("with everything below") stay
  readable on the colour of the selection.
- A node of a server's address space ticked through UI Automation, as a
  screen reader or voice control does it, is taken. The box showed the tick
  and the selection stayed empty, since the plugin followed only clicks.
- The certificates of the document server and of `uaaml serve --nodeset`
  name AMLOpcUa as their organisation. The OPC UA stack filled in
  "O=OPC Foundation" for a subject without one, and the dialog that asks to
  trust the server showed it. The stack kept such a certificate for the new
  subject; the server now makes its certificate anew when the subject
  differs, so a client that trusted the old one asks again once.
- The dialog "Trust this server?" gives the validity as yyyy-MM-dd, like
  every date of the plugin, not in the format of Windows' regional settings
  beside English texts.
- **New instance…** no longer asks for children of a MandatoryPlaceholder
  below an Optional child that is not ticked; that placeholder is not
  created, and names given for it were dropped without a word.
- **Edit** in the Modeler tab and **Publish…** find a model fetched from the
  OPC Foundation, the Cloud Library or a server, as an import does. They
  looked only in the modeler's models and the NodeSet folders.
- An import in the plugin reports what the command line reports: how many
  instances of the model were not taken, and the children that hang off
  their type through a reference Annex A does not follow. The plugin built
  its report without them. After **Apply to document** with the instances
  taken it no longer counts them as not taken, and says in the plugin's
  language where they went.
- "Save after import" (Settings) stays on after the editor is started again.
  Setting the toggles from the saved settings saved them in between, with
  this one still off.
- In the AML Editor the plugin finds the NodeSets it brings (the base model,
  DI, the AutomationML libraries) next to itself. It looked in the editor's
  folder, so it fetched the base model from the OPC Foundation although it
  has it, and the export could not write values of base model and DI
  structures.

## [0.1.3] - 2026-09-27

### Fixed

- The Modeler tab brings NodeSet.js 0.1.2: undo keeps a bounded number of
  steps, fewer for large models, instead of a full copy of the model for
  every step without end, which could exhaust the memory of the tab on a
  large companion specification; and a check that throws no longer stops the
  modeler.

## [0.1.2] - 2026-09-26

### Fixed

- The Modeler tab went blank when a DataType without fields (a subtype of
  Int32, say) was selected after one with fields, and unapplied changes were
  gone. The plugin now brings NodeSet.js 0.1.1, which fixes that and keeps the
  model through any failure of its interface: the tab says what went wrong
  and offers Save NodeSet and Try again, and the plugin still knows the model
  is unapplied.

## [0.1.1] - 2026-09-25

### Fixed

- The package of 0.1.0 came without the graphical modeler, so its Modeler tab
  had nothing to show. CI built the plugin with `-p:SkipModeler=true` and
  published what it built. It now builds NodeSet.js (pinned to its release
  v0.1.0) beside the plugin and refuses a package that lacks the modeler,
  the plugin, Opc2Aml or the NodeSets it brings along, or that carries the
  editor's contract assembly. The shared instantiation cases of NodeSet.js
  now run in CI as well.

## [0.1.0] - 2026-09-21

The first release: OPC UA information models in AutomationML, as a plugin for
the AutomationML Editor and as the command line tool `uaaml`.

### Added

- **Import** of OPC UA NodeSets as AML libraries by OPC 10000-83 (UAFX Offline
  Engineering) Annex A, using the OPC Foundation's own Opc2Aml, vendored with
  ten documented patches (`third_party/Opc2Aml/UPSTREAM.md`). Conversions are
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
- **The graphical modeler** [NodeSet.js](https://github.com/HamiedNabizada/NodeSet.js)
  in the Modeler tab, which opens a namespace of the document and imports the
  result back.
- **A guided tutorial** in the plugin, four lessons that watch the plugin's own
  state, and a "?" beside every part of the window for those who know OPC UA
  already.
- **Command line tool** `uaaml` with the same functions, so everything can be
  scripted and tested without the editor.
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

Found after these notes were first written on 20 September and fixed before
the tag:

- No NodeSet could be imported inside the AutomationML Editor: the conversion
  wrote its result as an `.amlx` container, and
  `AutomationMLContainer.AddRoot` of the Aml.Engine the editor brings along
  has another signature, so every import died with "Method not found". The
  conversion writes a plain AML file now, which nothing about it needed a
  container for (Opc2Aml patch 0010). Reading a document from an `.amlx` is
  untouched.
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

### Known limits

- The automated tests drive the plugin through a probe that hosts it in a
  window of its own; inside the AutomationML Editor it was tried by hand.
- The modeler is bundled from `NodeSet.js/dist/web`; a build without it
  (`-p:SkipModeler=true`) produces a plugin whose Modeler tab has nothing to
  show. The package published with this release was built that way by
  mistake; 0.1.1 carries the modeler.
- The document must be CAEX 3.0 (AutomationML 2.10); a 2.15 document is
  converted on import.
