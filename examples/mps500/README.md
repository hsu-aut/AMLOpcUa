# A plant to connect to: the MPS 500

`MPS500.NodeSet2.xml` is the information model of the MPS 500 learning factory
of the Helmut Schmidt University, as an OPC UA server of that plant would hold
it. Served, it stands in for the plant: the plugin connects to it, takes its
model and its running values into a document, and nothing has to be switched
on in a laboratory.

The structure follows `MPS500_PlantStructure.aml` (the stations ST10 to ST50,
the transfer system TS10, their modules and devices) and sits on OPC 10000-100
DI:

| Type | derives from | holds |
|---|---|---|
| `StationType` | DI `DeviceType` | `ParameterSet` (CycleCount, PartsProduced, CycleTime, Ready), `StationState`, `StationNumber`, and what DI asks of a device (Manufacturer, Model, SerialNumber) |
| `StationStateMachineType` | `FiniteStateMachineType` | Idle, Setup, Running, Faulted and the six transitions between them |
| `ModuleType` | DI `ComponentType` | `Ready` |
| `ActuatorType` | DI `ComponentType` | `Moving`, `Position` |
| `DriveType` | `ActuatorType` | `Speed` |
| `SensorType` | DI `ComponentType` | `Signal` |
| `ControllerType` | DI `ComponentType` | `Online`, `CpuLoad` |

`StationType` declares a `ParameterSet` of its own, which overrides DI's; an
instance keeps what both hold, as OPC UA asks (the fully inherited instance
declaration hierarchy, `docs/instances.md`).

## Serving it

```bash
uaaml serve --nodeset examples/mps500/MPS500.NodeSet2.xml --simulate
```

DI is loaded before it, because the model requires it. The server listens on
`opc.tcp://127.0.0.1:4840/AMLOpcUa`, the port OPC UA is registered for, offers
both models as the NamespaceFile of their namespace metadata, and with
`--simulate` lets the values move.

Then, in the plugin or on the command line:

```bash
# The model, from the server itself, into a document as AML libraries
uaaml nodeset opc.tcp://127.0.0.1:4840/AMLOpcUa http://hsu-hh.de/UA/MPS500/ --into plant.aml --insecure --accept

# The running plant into the same document, with types and current values
uaaml mirror opc.tcp://127.0.0.1:4840/AMLOpcUa "nsu=http://hsu-hh.de/UA/MPS500/;i=1041" plant.aml --insecure --accept
```

`--insecure` and `--accept` are for a server on this computer whose
certificate nobody trusted yet; a plant would be connected to securely.

## Plan and running plant in one document

`plan.py` writes the NodeId of the served node onto every element of
`MPS500_PlantStructure.aml` that the model also holds (68 of them). Mirroring
the server into that document then links each mirrored element to its planned
element with `refBaseObj`, and the plugin says so: "68 linked to their planned
element". Where the plan models a station with the plant's own class and the
server calls it `StationType`, the difference is reported rather than hidden.

```bash
python plan.py                      # writes C:\Dev\Demo\opcua\MPS500_Plan.aml
python plan.py <in.aml> <out.aml>   # or anywhere else
```

## Building it again

```bash
node build.mjs
```

The script builds the model with the NodeSet.js core (`../../../NodeSet.js`,
built with `npm run build:lib`) and checks it before writing. Change the plant
there, not in the XML.
