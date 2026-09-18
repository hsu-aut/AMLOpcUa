# Bundled NodeSets

Shipped with the command line tool and the plugin, because every import needs
the UA base model and most companion specifications build on DI.

| File | Model | Version | Published | Source |
|---|---|---|---|---|
| `Opc.Ua.NodeSet2.xml` | `http://opcfoundation.org/UA/` | 1.05.07 | 2026-05-01 | [UA-Nodeset](https://github.com/OPCFoundation/UA-Nodeset), tag `UAFX-1.00.04-2026-07-22`, `Schema/` |
| `Opc.Ua.Di.NodeSet2.xml` | `http://opcfoundation.org/UA/DI/` | 1.05.0 | 2025-11-15 | same tag, `DI/` |

License: OPC Foundation MIT License 1.00 (in the file headers).

A folder given with `--search` (command line) or under "NodeSet folders"
(plugin) can hold other versions; the newer publication of a model wins.
