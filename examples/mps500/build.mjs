// Builds the information model of the MPS 500 learning factory as a NodeSet2
// file, so that `uaaml serve --nodeset` can play the plant that is not there.
//
// The structure follows MPS500_PlantStructure.aml (stations, modules,
// devices, controllers) and sits on OPC 10000-100 DI: a station is a
// DeviceType, a module and a device are ComponentTypes, and every station
// carries a finite state machine of its own.
//
// It is written with the NodeSet.js core, which needs no browser:
//   node build.mjs [<output>]
// The default output is MPS500.NodeSet2.xml beside this script.

import { mkdirSync, writeFileSync } from 'node:fs';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { createRequire } from 'node:module';

const here = dirname(fileURLToPath(import.meta.url));
const require = createRequire(import.meta.url);
const core = require(resolve(here, '../../../NodeSet.js/dist/lib/nodeset.core.cjs'));
const { Workspace, writeNodeSet, check, REF, RULE } = core;

const DI = 'http://opcfoundation.org/UA/DI/';
const MPS = 'http://hsu-hh.de/UA/MPS500/';

const ws = new Workspace();
await ws.create(MPS);
await ws.addBundled(DI);
const e = ws.editor;
e.setModelInfo('1.0.0', '2026-09-20');

/** A type of a loaded model, by BrowseName. */
function type(namespaceUri, name) {
  const found = ws.space.ofClass('ObjectType').find(
    n => n.browseName.namespaceUri === namespaceUri && n.browseName.name === name);
  if (!found) throw new Error(`${name} not found in ${namespaceUri}`);
  return found.id;
}

const DeviceType = type(DI, 'DeviceType');
const ComponentType = type(DI, 'ComponentType');

// ---------------------------------------------------------------- the types

/** A station's state machine: what an operator sees on the panel. */
const StationState = e.addStateMachineType('StationStateMachineType');
e.setDescription(StationState, 'The states a station of the MPS 500 runs through.');
const idle = e.addState(StationState, 'Idle', 1);
const setup = e.addState(StationState, 'Setup', 2);
const running = e.addState(StationState, 'Running', 3);
const faulted = e.addState(StationState, 'Faulted', 4);
e.addTransition(StationState, 'IdleToSetup', 1, idle, setup);
e.addTransition(StationState, 'SetupToIdle', 2, setup, idle);
e.addTransition(StationState, 'IdleToRunning', 3, idle, running);
e.addTransition(StationState, 'RunningToIdle', 4, running, idle);
e.addTransition(StationState, 'RunningToFaulted', 5, running, faulted);
e.addTransition(StationState, 'FaultedToIdle', 6, faulted, idle);

/** Adds a variable below a declaration, with its DataType and unit in the description. */
function variable(parent, name, dataType, description, rule = RULE.Mandatory) {
  const v = e.addDeclaration(parent, 'Variable', name, rule);
  e.setDataType(v, dataType);
  if (description) e.setDescription(v, description);
  return v;
}

const ua = n => `http://opcfoundation.org/UA/|i=${n}`;
const Boolean_ = ua(1), Double_ = ua(11), UInt32 = ua(7), String_ = ua(12);

/** A station of the line: a DI device that holds modules. */
const StationType = e.addType('ObjectType', 'StationType', DeviceType);
e.setDescription(StationType, 'One station of the MPS 500, a device of its own with modules below it.');
{
  // DI declares ParameterSet as an Optional child of TopologyElementType.
  // Declaring it here overrides that declaration; an instance keeps what both
  // hold (OPC 10000-3, the fully inherited instance declaration hierarchy).
  const ps = e.addDeclaration(StationType, 'Object', 'ParameterSet');
  variable(ps, 'CycleCount', UInt32, 'Workpieces the station has started since it was switched on.');
  variable(ps, 'PartsProduced', UInt32, 'Workpieces the station has finished.');
  variable(ps, 'CycleTime', Double_, 'Seconds one workpiece takes, averaged over the last ten.');
  variable(ps, 'Ready', Boolean_, 'The station is ready for the next workpiece.');
  const state = e.addDeclaration(StationType, 'Object', 'StationState');
  e.setTypeDefinition(state, StationState);
  e.setDescription(state, 'What the station is doing.');
  variable(StationType, 'StationNumber', String_, 'The number in the line, as the plant structure names it.', RULE.Mandatory);
}

/** A mechanical module of a station: magazine, transfer arm, drilling unit. */
const ModuleType = e.addType('ObjectType', 'ModuleType', ComponentType);
e.setDescription(ModuleType, 'A module of a station, which holds the devices that move.');
variable(ModuleType, 'Ready', Boolean_, 'The module is ready.');

/** A device that moves something. */
const ActuatorType = e.addType('ObjectType', 'ActuatorType', ComponentType);
e.setDescription(ActuatorType, 'A cylinder, a drive or a gripper.');
variable(ActuatorType, 'Moving', Boolean_, 'The actuator is moving.');
variable(ActuatorType, 'Position', Double_, 'Where it stands, in millimetres or degrees.');

/** A drive, which also turns. */
const DriveType = e.addType('ObjectType', 'DriveType', ActuatorType);
e.setDescription(DriveType, 'An actuator with a speed of its own.');
variable(DriveType, 'Speed', Double_, 'Revolutions per minute.');

/** A device that only reports. */
const SensorType = e.addType('ObjectType', 'SensorType', ComponentType);
e.setDescription(SensorType, 'A sensor that says whether something is there.');
variable(SensorType, 'Signal', Boolean_, 'What the sensor reports.');

/** A controller of a station. */
const ControllerType = e.addType('ObjectType', 'ControllerType', ComponentType);
e.setDescription(ControllerType, 'A PLC or a valve terminal.');
variable(ControllerType, 'Online', Boolean_, 'The controller answers.');
variable(ControllerType, 'CpuLoad', Double_, 'Per cent of the cycle time used.');

// ------------------------------------------------------------ the instances

/**
 * The plant of the bridging example (`BridgingExample_MPS500.aml`), whose
 * plant view is coarser: one element per station, no modules. The server
 * knows more than the plan does, which is the normal case; the links are made
 * where the names meet.
 */
const bridging = {
  RawStorageThermometers: {
    description: 'Holds the thermometer housings and hands them over.',
    model: 'Station Verteilen',
    modules: {
      MD_Magazine: { DV_Feeder_cylinder: 'Actuator', DV_Magazine_empty_sensor: 'Sensor' },
    },
  },
  RawStorageCylinders: {
    description: 'Holds the cylinders and hands them over.',
    model: 'Station Verteilen',
    modules: {
      MD_Magazine: { DV_Feeder_cylinder: 'Actuator', DV_Part_present_sensor: 'Sensor' },
    },
  },
  ProcessingStation: {
    description: 'Drills the housing on the rotary indexing table.',
    model: 'Station Bearbeiten',
    modules: {
      MD_Rotary_indexing_table: { DV_Index_drive: 'Drive', DV_Index_position_sensor: 'Sensor' },
      MD_Clamping_unit: { DV_Clamp_cylinder: 'Actuator' },
      MD_Drilling_unit: { DV_Drill_spindle: 'Drive', DV_Linear_axis_Z: 'Drive', DV_Z_endpos_sensor: 'Sensor' },
    },
  },
  QualityCheckStation: {
    description: 'Checks the hole and reports the result.',
    model: 'Station Pruefen',
    modules: {
      MD_Testing_unit: { DV_Probe_cylinder: 'Actuator', DV_Depth_probe: 'Sensor' },
    },
  },
  RoboticAssemblyStation: {
    description: 'Puts the cover on and screws it down.',
    model: 'Station Montieren',
    modules: {
      MD_Robot: { DV_Robot_arm: 'Drive', DV_Robot_controller: 'Controller', DV_Assembly_gripper: 'Actuator' },
    },
  },
  BufferStorage: {
    description: 'Buffers the workpieces between the stations.',
    model: 'Puffer',
    modules: {
      MD_Parts_buffer: { DV_Buffer_sensor: 'Sensor' },
    },
  },
  ShippingArea: {
    description: 'Carries the finished workpieces out.',
    model: 'Transfersystem',
    modules: {
      MD_Conveyor_loop: { DV_Conveyor_motor: 'Drive', DV_Stopper: 'Actuator' },
    },
  },
  Controller: {
    description: 'The control of the line.',
    model: 'Leitsteuerung',
    modules: {
      MD_Control: { CTRL_PLC: 'Controller', CTRL_ValveTerminal: 'Controller' },
    },
  },
};

/** The plant, as MPS500_PlantStructure.aml holds it. */
const learningFactory = {
  ST10_Distributing: {
    description: 'Separates the housings from the magazine and hands them over.',
    model: 'Station Verteilen',
    modules: {
      MD_Magazine: { DV_Ejection_cylinder: 'Actuator', DV_Magazine_empty_sensor: 'Sensor', DV_Ejection_endpos_sensor: 'Sensor' },
      MD_Transfer_arm: { DV_Swivel_drive: 'Drive', DV_Vacuum_gripper: 'Actuator' },
      MD_Control: { CTRL_PLC: 'Controller', CTRL_ValveTerminal: 'Controller' },
    },
  },
  ST20_PartsDistributing: {
    description: 'Feeds the inserts and places them into the housing.',
    model: 'Station Teile verteilen',
    modules: {
      MD_Parts_magazine: { DV_Feeder_cylinder: 'Actuator', DV_Part_present_sensor: 'Sensor' },
      MD_Pick_and_place: { DV_Linear_axis_X: 'Drive', DV_Lift_cylinder: 'Actuator', DV_Parallel_gripper: 'Actuator' },
      MD_Control: { CTRL_PLC: 'Controller', CTRL_ValveTerminal: 'Controller' },
    },
  },
  ST30_Processing: {
    description: 'Drills the housing on the rotary indexing table and checks the hole.',
    model: 'Station Bearbeiten',
    modules: {
      MD_Rotary_indexing_table: { DV_Index_drive: 'Drive', DV_Index_position_sensor: 'Sensor' },
      MD_Clamping_unit: { DV_Clamp_cylinder: 'Actuator' },
      MD_Drilling_unit: { DV_Drill_spindle: 'Drive', DV_Linear_axis_Z: 'Drive', DV_Z_endpos_sensor: 'Sensor' },
      MD_Testing_unit: { DV_Probe_cylinder: 'Actuator', DV_Depth_probe: 'Sensor' },
      MD_Handling_unit: { DV_Transfer_cylinder: 'Actuator' },
      MD_Control: { CTRL_PLC: 'Controller', CTRL_ValveTerminal: 'Controller' },
    },
  },
  ST40_Assembly: {
    description: 'Puts the cover on and screws it down.',
    model: 'Station Montieren',
    modules: {
      MD_Robot: { DV_Robot_arm: 'Drive', DV_Robot_controller: 'Controller', DV_Assembly_gripper: 'Actuator' },
      MD_Parts_buffer: { DV_Buffer_sensor: 'Sensor' },
      MD_Control: { CTRL_PLC: 'Controller', CTRL_ValveTerminal: 'Controller' },
    },
  },
  ST50_Storage: {
    description: 'Stores the finished workpieces in the rack.',
    model: 'Station Lagern',
    modules: {
      MD_Rack: {},
      MD_Stacker_crane: { DV_Stacker_axis_X: 'Drive', DV_Stacker_axis_Z: 'Drive', DV_Storage_gripper: 'Actuator' },
      MD_Control: { CTRL_PLC: 'Controller', CTRL_ValveTerminal: 'Controller' },
    },
  },
  TS10_Transfer_system: {
    description: 'Carries the pallets from station to station.',
    model: 'Transfersystem',
    modules: {
      MD_Conveyor_loop: { DV_Conveyor_motor: 'Drive', DV_Stopper: 'Actuator' },
      MD_Identification: { DV_RFID_head: 'Sensor' },
      MD_Pallet_pool: {},
      MD_Control: { CTRL_PLC: 'Controller' },
    },
  },
};

const deviceTypes = { Actuator: ActuatorType, Drive: DriveType, Sensor: SensorType, Controller: ControllerType };

// Which plant is served: the learning factory by default, the coarser view of
// the bridging example with --bridging.
const coarse = process.argv.includes('--bridging');
const plant = coarse ? bridging : learningFactory;

/** The child of a node by BrowseName, whatever holds it. */
function child(parent, name) {
  const found = ws.space.children(ws.space.get(parent)).find(c => c.node.browseName.name === name);
  if (!found) throw new Error(`${name} not found below ${ws.space.get(parent).browseName.name}`);
  return found.node.id;
}

/** Sets a value below a node, by path. */
function value(root, path, text) {
  let node = root;
  for (const step of path.split('/')) node = child(node, step);
  e.setValue(node, text);
  return node;
}

// The line, as one object below the Objects folder.
const line = e.instantiate(type('http://opcfoundation.org/UA/', 'FolderType'), 'MPS500');
e.setDescription(line, 'The MPS 500 learning factory of the Helmut Schmidt University.');

let counter = 0;
const numbers = () => ++counter;
for (const [stationName, station] of Object.entries(plant)) {
  const s = e.instantiate(StationType, stationName, { parent: line, referenceType: REF.Organizes });
  e.setDescription(s, station.description);
  value(s, 'StationNumber', stationName.slice(2, 4));
  value(s, 'Manufacturer', 'Festo Didactic');
  value(s, 'Model', station.model);
  value(s, 'SerialNumber', `MPS500-${stationName.slice(0, 4)}-${1000 + numbers()}`);
  value(s, 'ParameterSet/CycleCount', String(120 + numbers() * 7));
  value(s, 'ParameterSet/PartsProduced', String(100 + numbers() * 5));
  value(s, 'ParameterSet/CycleTime', (8 + numbers() * 0.4).toFixed(1));
  value(s, 'ParameterSet/Ready', 'true');
  value(s, 'StationState/CurrentState', 'Running');

  for (const [moduleName, devices] of Object.entries(station.modules)) {
    const m = e.instantiate(ModuleType, moduleName, { parent: s, referenceType: REF.HasComponent });
    value(m, 'Ready', 'true');
    for (const [deviceName, kind] of Object.entries(devices)) {
      const d = e.instantiate(deviceTypes[kind], deviceName, { parent: m, referenceType: REF.HasComponent });
      if (kind === 'Sensor') {
        value(d, 'Signal', numbers() % 2 === 0 ? 'true' : 'false');
      } else if (kind === 'Controller') {
        value(d, 'Online', 'true');
        value(d, 'CpuLoad', (20 + (numbers() % 7) * 5).toFixed(1));
      } else {
        value(d, 'Moving', 'false');
        value(d, 'Position', (numbers() % 90).toFixed(1));
        if (kind === 'Drive') value(d, 'Speed', String(600 + (numbers() % 10) * 120));
      }
    }
  }
}

// ---------------------------------------------------------------- write out

const added = ws.syncRequiredModels();
const findings = check(ws.space, ws.editable);
const errors = findings.filter(f => f.severity === 'error');
for (const f of findings) console.log(`${f.severity === 'error' ? 'ERROR' : 'warn '} ${f.rule} ${f.message}`);

const named = process.argv.slice(2).find(a => !a.startsWith('--'));
const out = named ? resolve(named) : join(here, coarse ? 'bridging' : '.', 'MPS500.NodeSet2.xml');
mkdirSync(dirname(out), { recursive: true });
writeFileSync(out, writeNodeSet(ws.editable), 'utf8');
console.log(`${ws.editable.nodes.length} nodes, requires ${added.join(', ')}`);
console.log(`${findings.length} findings, ${errors.length} of them errors`);
console.log(out);
if (errors.length > 0) process.exitCode = 1;
