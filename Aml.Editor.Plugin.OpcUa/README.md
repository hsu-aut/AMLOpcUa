# AMLOpcUa

OPC UA for the [AutomationML Editor](https://www.automationml.org/).

- **Import:** OPC UA NodeSets become AML libraries according to OPC 10000-83
  (UAFX Offline Engineering), Annex A. Companion specifications come from the
  OPC Foundation or the UA Cloud Library; the base model and DI are included.
- **Instances:** instances of UA types with their Mandatory children, chosen
  Optional children and named children for placeholders, and a check of
  instances against their types.
- **Servers:** connect to an OPC UA server, import its types, take its nodes
  into the document with their NodeIds and UA types, read values once or keep
  them live in the document; serve the open document as an OPC UA server.
- **Practice plant:** the MPS 500 learning factory as an OPC UA server on this
  computer, with moving values, to try all of this without a plant.
- **Export:** AML documents (CAEX 2.15 and 3.0) as OPC UA NodeSets by the
  rules of the AutomationML/OPC Foundation working group, and ModelDesign for
  the OPC Foundation's ModelCompiler.
- **Diagrams** of UA types and instances in OPC 10000-3 notation, and the
  graphical modeler [NodeSet.js](https://github.com/HamiedNabizada/NodeSet.js)
  in a tab of its own.
- English and German.

## Installing

1. In the AutomationML Editor (6.4 or later), open the PlugIn Manager, choose
   the source **PublicSource**, select **AMLOpcUa** among the available
   plugins and install it.
2. Restart the editor and switch the plugin on once with **PlugIns** ›
   **AMLOpcUa** › **Activate**.

Without a network, download the package from the
[releases](https://github.com/hsu-aut/AMLOpcUa/releases) and add its folder as a
package source in the PlugIn Manager; the package asks for nothing the editor
does not bring.

## More

- [User manual](https://github.com/hsu-aut/AMLOpcUa/blob/main/docs/manual.md)
- [Changes](https://github.com/hsu-aut/AMLOpcUa/blob/main/CHANGELOG.md)
- [Source](https://github.com/hsu-aut/AMLOpcUa), MIT license;
  [third-party notices](https://github.com/hsu-aut/AMLOpcUa/blob/main/THIRD-PARTY-NOTICES.md)

The conversion is done by [Opc2Aml](https://github.com/OPCF-Members/Opc2Aml),
the OPC Foundation's reference implementation of Annex A.
