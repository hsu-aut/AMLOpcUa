// The names the OPC Foundation publishes its specifications under
// (reference.opcfoundation.org), for the folders of UA-Nodeset that abbreviate
// them. Someone who looks for "Device Integration" does not think of "DI";
// folders that already carry the name ("Machinery", "Robotics") need no entry.

namespace OpcUaAml.NodeSets;

public static class SpecificationTitles
{
    private static readonly Dictionary<string, string> ByFolder = new(StringComparer.OrdinalIgnoreCase)
    {
        ["ADI"] = "Analyser Devices",
        ["AMB"] = "Asset Management Basics",
        ["AML"] = "AutomationML",
        ["AutoID"] = "AutoID Devices",
        ["CAS"] = "Compressed Air Systems",
        ["CSPPlusForMachine"] = "Control & Communication System Profile for Machine (CSP+)",
        ["CuttingTool"] = "Cutting Tools",
        ["DEXPI"] = "Data Exchange in the Process Industry (DEXPI)",
        ["DI"] = "Devices (Device Integration)",
        ["ECM"] = "Energy Consumption Management",
        ["FDI"] = "Field Device Integration (FDI)",
        ["GDS"] = "Global Discovery Server",
        ["Glass"] = "Flat Glass Processing",
        ["GMS"] = "Geometric Measurement Systems",
        ["GPOS"] = "Global Positioning",
        ["I4AAS"] = "Asset Administration Shell (AAS)",
        ["IA"] = "Industrial Automation",
        ["IJT"] = "Joining Systems and Tightening Systems (Industrial Joining Technologies)",
        ["IOLink"] = "IO-Link Devices and IO-Link Masters",
        ["IREDES"] = "International Rock Excavation Data Exchange Standard (IREDES)",
        ["ISA-95"] = "ISA-95 Common Object Model",
        ["ISA95-JOBCONTROL"] = "ISA-95 Job Control",
        ["LADS"] = "Laboratory and Analytical Device Standard (LADS)",
        ["MachineTool"] = "Machine Tools",
        ["MDIS"] = "MCS-DCS Interface Standardization (MDIS)",
        ["OpenSCS"] = "OPEN-SCS Serialization",
        ["PADIM"] = "Process Automation Devices (PA-DIM)",
        ["PAEFS"] = "Process Air Extraction and Filtration Systems (PAEFS)",
        ["PlasticsRubber"] = "Plastics and Rubber Machinery",
        ["PNDRV"] = "PROFINET Drives",
        ["PNEM"] = "PROFINET PROFIenergy",
        ["PNENC"] = "PROFINET Encoder",
        ["PNGSDGM"] = "PROFINET GSD Generic Model",
        ["PNRIO"] = "PROFINET Remote IO",
        ["Pumps"] = "Pumps and Vacuum Pumps",
        ["RSL"] = "Relative Spatial Location",
        ["Scales"] = "Weighing Technology",
        ["Schema"] = "Base information model",
        ["TMC"] = "Tobacco Machine Communication",
        ["TTD"] = "Textile Testing Devices",
        ["UAFX"] = "Field eXchange (UAFX)",
        ["WireHarness"] = "Wire Harness Manufacturing",
        ["WMTP"] = "Wireless Machine Tool Peripherals",
        ["WoT"] = "Web of Things (WoT) Connectivity",
        ["XML"] = "XML Data Type Mapping",
    };

    /// <summary>The published name for a folder of the repository ("DI", "PlasticsRubber/GeneralTypes/1.03"); null when the folder is the name.</summary>
    public static string? Of(string folder)
    {
        if (ByFolder.TryGetValue(folder, out var title)) return title;
        var first = folder.Split('/')[0];
        return ByFolder.TryGetValue(first, out title) ? title : null;
    }
}
