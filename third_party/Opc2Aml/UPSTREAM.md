# Opc2Aml, vendored

| | |
|---|---|
| Upstream | https://github.com/OPCF-Members/Opc2Aml |
| Commit | `9d8fa9617f41bf76dd4036f4da369826a9d214ac` (2026-01-13, "139 invalid parent node (#149)") |
| License | MIT, see [LICENSE](LICENSE) |
| Included | The library project only: `*.cs`, `Opc2Aml.csproj`, `Properties/`, `UANodeSet.xsd`, `app.config.json`, `README.md`, `LICENSE`. Not the console (`Opc2AmlConsole/`) or the tests (`SystemTest/`). |

Opc2Aml is not published on NuGet and is used here as a library, so the source
is part of this repository. Every change to it is a numbered patch in
[`../patches/`](../patches), marked `AMLOpcUa patch NNNN` in the code, so it
can be reported upstream and reapplied after an update.

| Patch | Why |
|---|---|
| 0001 skip unresolvable non-hierarchical references | DI 1.05.0 made `ConnectsTo` non-hierarchical. The placeholders `<CPIdentifier>` (under `NetworkType`) and `<NetworkIdentifier>` (under `ConnectionPointType`) are attached to their type only through it, so Opc2Aml finds no element for the reference and throws a `NullReferenceException` for every NodeSet that depends on DI 1.05.0. The patch skips such a reference and reports it in a new `NodeSetToAML.Warnings` list. |
| 0002 OPC UA stack 1.5.378 under MIT | Opc2Aml references OPC UA .NET Standard 1.5.375.443, published under the OPC Foundation's dual license (RCL/GPL 2.0). 1.5.378 is the first release under MIT. It seals `ExpandedNodeId`, from which `AmlExpandedNodeId` derived; the class now wraps one. IDs are unchanged: the test `Class_IDs_are_identical_to_the_published_library` compares them with the libraries the OPC Foundation generated with 1.5.375. |
| 0003 model order without cycles | `OrderModelInfo.AddCompiled` collected the requirements of every model in a NodeSet file for each of them and recursed without a visited check. A NodeSet that declares several models requiring each other (an AML document exported by the AML-UA-XSLT rules declares one per library) sent it into endless recursion and a stack overflow. Now only the model's own entry counts, and a model already collected is not entered again. |
| 0004 instances only below Objects | `CreateInstances` follows every hierarchical reference below the Objects folder and treats each target as an instance. The AutomationML file view of OPC 30040 and of the AML-UA-XSLT rules organizes the AML classes, which are ObjectTypes, in folders below Objects; an ObjectType has no type definition, so the conversion failed with a NullReferenceException. Type nodes are now skipped there; the type libraries cover them. |

| 0005 ordinal string comparison | `IsolateNodeId` and `GetNodeIdPrefix` run for every link and interface ID and compared with the culture aware `StartsWith` and `IndexOf`, which in .NET 8 go through ICU: about 1.5 s of a 17 s DI conversion. IDs are URL encoded ASCII, so an ordinal comparison gives the same result. |

## Updating

1. Copy the library files of the new upstream commit over this folder.
2. Apply the patches in order (`git apply ../patches/NNNN-*.patch` from this
   folder); drop a patch that upstream has made unnecessary.
3. Run `dotnet test dotnet/OpcUaAml.Tests`, update the commit above.
