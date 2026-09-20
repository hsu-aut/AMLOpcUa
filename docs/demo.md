# Demo

A run through the whole toolchain in about twelve minutes, in the order that
tells the story: a model is built, it becomes AutomationML, it becomes a
server, and it goes back out to the OPC UA world. Every step has a fallback,
because a demo that needs the network is a demo that fails.

Prepare beforehand: a document with DI imported, the modeler built
(`npm run build` in `NodeSet.js`), the ModelCompiler installed if step 6 is
shown (`dotnet tool install --global OPCFoundation.Opc.Ua.ModelCompiler.Tool`),
and the plugin's log window open on the Namespaces tab, because it answers the
question "what did it just do" without leaving the screen.

## 1. A model out of nothing (3 min, NodeSet.js)

1. **New model…**, namespace `http://example.org/Pump/`, version 1.0.0, DI
   ticked as a model it builds on. Say why: a companion specification never
   starts at zero, it starts at DI.
2. `+` on ObjectTypes, name `PumpType`. The canvas draws it in the notation of
   OPC 10000-3 Annex C, not in a notation of our own.
3. Add child: Variable `FlowRate`, DataType Double, ModellingRule Mandatory.
   Add child: Method `Start`, one input argument `Speed`, Int32.
4. `+ machine` on ObjectTypes, name `PumpStateMachineType`. States Idle,
   Running, Fault. Transitions IdleToRunning, RunningToFault, FaultToIdle,
   the first one caused by `Start()`. The state chart draws itself while you
   type.
   Say what it writes: components of StateType and TransitionType, StateNumber
   and TransitionNumber as Mandatory properties of the UA namespace, FromState,
   ToState and HasCause in both directions, exactly as the base model writes
   its own machines (OPC 10000-5 Annex B).
5. **Checks: 0 errors** in the toolbar. Open it once: 21 rules on the NodeSet
   itself, each finding with its rule and a sentence.
6. **Save NodeSet** and open the file in an editor for three seconds. It is a
   plain NodeSet2 file, and the layout sits in `Extensions`, where other tools
   ignore it.

*Fallback:* the screenshots in the handover folder show every step.

## 2. The same model in AutomationML (2 min, plugin)

1. Import the NodeSet into the document. The libraries appear: SUC per
   ObjectType, AttributeType per DataType, InterfaceClass per ReferenceType.
   That is OPC 10000-83 Annex A, and the conversion is the OPC Foundation's own
   Opc2Aml, vendored with eight patches we wrote.
2. Namespaces tab, select the model: what it brings, what it builds on, what
   uses it, its types.
3. **New instance** of PumpType: every Mandatory child appears, the Optional
   ones are ticked, a placeholder is filled by name.
4. **Check**: the instance against its type.

## 3. A running server (2 min, plugin)

1. Server tab, **Serve**. The document itself becomes an OPC UA server, local
   only by default; the network needs a tick and then only accepts clients
   whose certificate is trusted.
2. **Simulate** ticked: the numbers swing around the document's values.
3. Connect with UaExpert or with `uaaml browse opc.tcp://127.0.0.1:48400/AMLOpcUa`
   and walk to the pump.

*Fallback:* `uaaml serve plant.aml --simulate` in a terminal shows the same
thing without the editor.

## 4. Companion specifications without an account (1 min)

**Companion specs…**: the list comes from OPCFoundation/UA-Nodeset on GitHub,
every released specification, no login, no API key. Pick Machinery, import it
with the models it requires. The Cloud Library, which needs an account, is one
click away for the models that live only there.

## 5. Documentation (1 min)

**Documentation…** on a namespace writes one HTML file: every type with its
declarations and a diagram, the DataTypes with their fields, the
ReferenceTypes. Nothing external, so it can be mailed.

## 6. Out to the OPC UA world again (2 min)

**ModelDesign…** writes the model in the form the OPC Foundation's
ModelCompiler reads, with the identifier file beside it, so the NodeIds
survive. Run the compiler and the model comes back as a NodeSet. The other
direction needs no button: a ModelDesign dropped on the plugin is compiled and
imported.

Say the honest number here: 414 of DI's 447 own nodes come back identical
through that round trip. The rest are the nodes the compiler generates and
numbers itself.

## Numbers that hold up

| What | Number | Where it comes from |
|---|---|---|
| Tests | 293 C# and 81 TypeScript | `dotnet test`, `npx vitest run` |
| Companion specifications measured | 34 | `uaaml roundtrip --inverse`, docs/roundtrip.md |
| Facts surviving UA → AML → UA | 94.9 % | same run |
| Lost on the way back (our side) | 0.17 % | same run; the rest is Annex A |
| DI through ModelDesign and back | 417 of 447 nodes identical | docs/modeldesign.md |
| A state machine through ModelDesign and back | 16 of 16 nodes identical | docs/modeldesign.md |
| The same machine through AutomationML and back | every node, defaults aside | `uaaml compare` |
| Opc2Aml defects found and patched | 4 of 8 patches | third_party/Opc2Aml/UPSTREAM.md |
| Rules on a NodeSet in the modeler | M001 to M021 | src/nodeset/checks.ts |
| Cold conversion of DI, then cached | 15 s, then 0.7 s | conversion cache |

## What not to promise

- No code generation. The ModelCompiler does that, and we only hand it a design.
- Not certified. The OPC Foundation certifies products; this is a tool that
  writes files their tools read.
- The AML → UA direction follows the working group's AML-UA-XSLT rules with 17
  documented deviations (`docs/export.md`), not a published standard.
