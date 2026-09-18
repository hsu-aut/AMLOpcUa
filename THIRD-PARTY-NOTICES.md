# Third-party notices

This project includes or redistributes the following components. The license
of this project (MIT, see [LICENSE](LICENSE)) does not apply to them; each is
used under its own license, referenced below.

## Source code included in this repository

| Component | Version | License | Copyright |
|---|---|---|---|
| [Opc2Aml](https://github.com/OPCF-Members/Opc2Aml), in `third_party/Opc2Aml/` | commit 9d8fa96 (2026-01-13), with the patches in `third_party/patches/` | MIT | 2021 OPC Foundation |

## Data files included in this repository and in the plugin package

| Component | Version | License | Copyright |
|---|---|---|---|
| OPC UA base NodeSet `Opc.Ua.NodeSet2.xml`, from [UA-Nodeset](https://github.com/OPCFoundation/UA-Nodeset) | 1.05.07 (2026-05-01) | OPC Foundation MIT License 1.00 | 2005-2026 The OPC Foundation, Inc. |
| OPC UA for Devices NodeSet `Opc.Ua.Di.NodeSet2.xml`, from UA-Nodeset | 1.05.0 (2025-11-15) | OPC Foundation MIT License 1.00 | 2005-2024 The OPC Foundation, Inc. |

Test fixtures in `dotnet/OpcUaAml.Tests/Fixtures/` are further NodeSets and
AML libraries from UA-Nodeset (tags `UA-1.05.05-2025-06-30`,
`DI-1.04.0-2022-11-03`, `UAFX-1.00.04-2026-07-22`), under the same license.
`Fixtures/aml-ua-xslt/` holds the unit tests of
[AML-UA-XSLT](https://github.com/AutomationML/AML-UA-XSLT) (commit a144dcc),
MIT, Copyright 2021 AutomationML e.V. They are not part of the plugin package.

## Redistributed in the plugin package

| Component | Version | License | Copyright |
|---|---|---|---|
| [OPC UA .NET Standard](https://github.com/OPCFoundation/UA-.NETStandard) (Opc.Ua.Core, .Types, .Client, .Server, .Configuration, .Security.Certificates, .Gds.Client.Common, .Gds.Server.Common) | 1.5.378.176 | OPC Foundation MIT License 1.00 | 2005-2025 OPC Foundation, Inc. |
| [BitFaster.Caching](https://github.com/bitfaster/BitFaster.Caching) | 2.6.0 | MIT | 2020 Alex Peck |
| [Newtonsoft.Json](https://github.com/JamesNK/Newtonsoft.Json) | 13.0.4 | MIT | 2007 James Newton-King |
| Microsoft.Extensions.Configuration, .Binder, .Json, .FileExtensions, .Abstractions, FileProviders, FileSystemGlobbing, Primitives, Logging, Options, DependencyInjection | 9.0.1 and 10.0.8 | MIT | .NET Foundation and Contributors |
| System.Text.Json, System.Text.Encodings.Web, System.IO.Pipelines, System.IO.Packaging, System.Collections.Immutable, System.Diagnostics.DiagnosticSource, System.Formats.Asn1 | 10.0.x | MIT | .NET Foundation and Contributors |

Version 1.5.378 is the first release of the OPC UA .NET Standard stack under
the MIT license. Opc2Aml itself references 1.5.375, which was published under
the OPC Foundation's dual license (RCL for members, GPL 2.0 otherwise); patch
0002 moves it to 1.5.378.

## Used at build time or provided by the AutomationML Editor, not redistributed

| Component | License |
|---|---|
| [Aml.Engine](https://github.com/AutomationML/AMLEngine2.1), Aml.Engine.Resources | MIT |
| Aml.Editor.Plugin.Contract, Aml.Editor.API, Aml.Skins | MIT |
| xunit, Microsoft.NET.Test.Sdk (tests only) | Apache-2.0, MIT |
