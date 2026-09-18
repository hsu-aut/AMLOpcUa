# Bundled NodeSets

Shipped with the command line tool and the plugin, because every import needs
the UA base model and most companion specifications build on DI.

| File | Model | Version | Published | Source |
|---|---|---|---|---|
| `Opc.Ua.NodeSet2.xml` | `http://opcfoundation.org/UA/` | 1.05.07 | 2026-05-01 | [UA-Nodeset](https://github.com/OPCFoundation/UA-Nodeset), tag `UAFX-1.00.04-2026-07-22`, `Schema/` |
| `Opc.Ua.Di.NodeSet2.xml` | `http://opcfoundation.org/UA/DI/` | 1.05.0 | 2025-11-15 | same tag, `DI/` |
| `Opc.Ua.AMLBaseTypes.NodeSet2.xml` | `http://opcfoundation.org/UA/AML/` | 1.00 | 2016-02-22 | [AML-UA-XSLT](https://github.com/AutomationML/AML-UA-XSLT) commit a144dcc, `UnitTests/`; the working group draft of the AML base types that every export requires |
| `Opc.Ua.AMLStandardLibraries.NodeSet2.xml` | `http://opcfoundation.org/UA/AML/AutomationMLInterfaceClassLib`, `.../AutomationMLBaseRoleClassLib` and the other standard libraries | 2.2.0 | 2026-01-01 | generated: `uaaml export dotnet/OpcUaAml.Tests/Fixtures/aml-ua-xslt/AML/1_AMLBaseLibraries.aml --date 2026-01-01`; `BundledNodeSetTests` fails when it drifts from the exporter |

License: OPC Foundation MIT License 1.00 (in the file headers); the AML
standard libraries NodeSet is derived from the AutomationML standard
libraries in the AML-UA-XSLT unit tests, MIT, AutomationML e.V.

The two AML NodeSets are needed to import what the export writes (the
round trip, [docs/roundtrip.md](../docs/roundtrip.md)).

A folder given with `--search` (command line) or under "NodeSet folders"
(plugin) can hold other versions; the newer publication of a model wins.
