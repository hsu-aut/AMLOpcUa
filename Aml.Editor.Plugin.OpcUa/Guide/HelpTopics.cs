// What the "?" buttons say: a few sentences per part of the plugin, the terms
// they use, and the lesson of the tutorial that walks through it. For people
// who know OPC UA and the plugin and are unsure about one thing; the tutorial
// is for those who start.

using Aml.Editor.Plugin.OpcUa.Localization;

namespace Aml.Editor.Plugin.OpcUa.Guide;

internal sealed record HelpTopic(string Title, string Text, string[] Terms, string? Lesson = null);

internal static class HelpTopics
{
    public static readonly IReadOnlyDictionary<string, HelpTopic> All = new Dictionary<string, HelpTopic>
    {
        ["namespaces"] = new(
            L.T("Namespaces in this document"),
            L.T("Each OPC UA model imported into the document, with its version and publication date. An import turns a NodeSet into AutomationML libraries by Annex A: attribute types (ATL_), interface classes (ICL_), role classes (RCL_) and system unit classes (SUC_) per namespace, together with the models it requires. Select a namespace to see what it brings, what it builds on and which elements use its types; Documentation… writes all of it as one HTML page with diagrams."),
            new[] { "NodeSet", "Namespace", "Annex A", "Companion specification" }, "models"),
        ["namespaces.commands"] = new(
            L.T("Import, export and more"),
            L.T("Import NodeSet, Companion specs (the OPC Foundation's published NodeSets, no account) and Cloud Library bring models in; a second import of a namespace updates it, an older version never replaces a newer one. Export writes the document as a NodeSet by the AML-UA-XSLT rules, or one imported model back as the nodes it was. New instance creates an instance of a UA type; Check compares instances with their types; Link VDI 3682 ties process elements to OPC UA objects and methods. Ctrl+Z in the editor does not undo the plugin's changes; save before."),
            new[] { "Annex A", "AML-UA-XSLT", "Cloud Library", "VDI 3682" }, "models"),
        ["check"] = new(
            L.T("Check"),
            L.T("Compares each instance in the instance hierarchies with its UA type: every Mandatory child present, every MandatoryPlaceholder filled, types known and not abstract, reference links only between the interfaces Annex A allows. Double click or Enter selects the element of a finding. Add missing Mandatory children brings instances up to date after a newer version of their types was imported; it shows what it adds first."),
            new[] { "ModellingRule", "Placeholder", "Abstract type" }, "instances"),
        ["server.connection"] = new(
            L.T("Connecting"),
            L.T("The endpoint URL of a running server, such as opc.tcp://host:4840. With Secure ticked only signed and encrypted endpoints are taken. A server certificate this computer does not know is shown with its fingerprint before you trust it; trusted, exactly that one is kept. User and password are only sent over a secured connection; empty means anonymous."),
            new[] { "Endpoint", "Security mode", "Certificate" }, "server"),
        ["server.commands"] = new(
            L.T("What to do with the server"),
            L.T("Types of the server imports the types of one of its namespaces. Take into document creates elements for the checked nodes, with their NodeIds, UA types and values; Count nodes says how many first. Bind to element gives an element of yours a node's address. Read values into document reads once, Keep document values live writes every change into the document, Watch only shows values in the list below. Remove server addresses saves a copy of the document without where its servers are reached (endpoints, server URIs, the ServerInstanceUri of every NodeId), to pass it on; the log names every place a host name still appears."),
            new[] { "NodeId", "Address space" }, "server"),
        ["server.take"] = new(
            L.T("What to take"),
            L.T("Check nodes in the address space; right click for how much below each: the node only, with its children, or everything below. Depth limits the levels of 'everything below', at most limits the number of nodes, into names the InstanceHierarchy. The filters leave out properties, variables or other namespaces. Instances of a type checks every instance of a type below a node. What was taken keeps its selection; Load kept selection brings it back."),
            new[] { "Address space", "Property", "View" }, "server"),
        ["server.serve"] = new(
            L.T("This document as a server"),
            L.T("Serves the document's instance hierarchies as an OPC UA server, to try clients before the plant exists. Values follow the document; added elements need a restart. With 'simulate values' numbers swing around the document's values and booleans toggle, as in a running plant. Only this computer can connect, unless 'to the network' is ticked: then other computers may, over secured connections and only with a certificate you trusted under Clients…."),
            new[] { "Endpoint", "Certificate" }, "server"),
        ["diagram"] = new(
            L.T("Diagram"),
            L.T("A UA type or instance in the notation of OPC 10000-3. Shapes: rectangle Object, rounded rectangle Variable, ellipse Method; types are shaded. Lines: one stroke HasComponent, two strokes HasProperty, two filled heads HasTypeDefinition, two hollow heads HasSubtype, an open head other hierarchical references, a filled head the others. Letters give the ModellingRule: M Mandatory, O Optional, MP and OP placeholders, E ExposesItsArray."),
            new[] { "ReferenceType", "ModellingRule", "Placeholder" }, "models"),
        ["modeler"] = new(
            L.T("Modeler"),
            L.T("Edits NodeSets, not the document. Edit opens the NodeSet a namespace was imported from, New model starts an empty one, Open NodeSet file any other. The modeler's Apply to document imports the result like any NodeSet; its Save NodeSet writes the file. 'Modeler *' on the tab means changes neither applied nor saved; the plugin asks before it drops them."),
            new[] { "NodeSet", "Namespace", "ModellingRule" }, "modeler"),
    };

    /// <summary>Term (as topics name it) to the name shown and its explanation.</summary>
    public static readonly IReadOnlyDictionary<string, (string Name, string Text)> Glossary = new Dictionary<string, (string Name, string Text)>
    {
        ["NodeSet"] = (L.T("NodeSet"), L.T("An OPC UA information model as an XML file (UANodeSet): its types, instances and the models it requires.")),
        ["Namespace"] = (L.T("Namespace"), L.T("The URI that owns a set of nodes, such as http://opcfoundation.org/UA/DI/ for Device Integration. Every model has one, and every NodeId names it.")),
        ["Companion specification"] = (L.T("Companion specification"), L.T("An information model for a domain, published by the OPC Foundation with a partner organisation: DI, Machinery, Robotics and many more.")),
        ["Annex A"] = (L.T("Annex A"), L.T("OPC 10000-83 Annex A: how an OPC UA model becomes AutomationML libraries. The plugin imports by it, with Opc2Aml, the OPC Foundation's implementation.")),
        ["AML-UA-XSLT"] = (L.T("AML-UA-XSLT"), L.T("The rules of the joint AutomationML and OPC Foundation working group for the other direction, from AML to OPC UA; the successor of OPC 30040. Export uses them.")),
        ["Cloud Library"] = (L.T("Cloud Library"), L.T("The OPC Foundation's online catalogue of information models (uacloudlibrary.opcfoundation.org). Searching it needs an account or an API key.")),
        ["VDI 3682"] = (L.T("VDI 3682"), L.T("Formalised process description: products, energy and information, process operators and technical resources. Link VDI 3682 ties them to OPC UA objects and methods.")),
        ["ModellingRule"] = (L.T("ModellingRule"), L.T("Says whether a child declared by a type appears in each instance: Mandatory always, Optional when chosen, placeholders as often as you name children for them.")),
        ["Placeholder"] = (L.T("Placeholder"), L.T("A child declaration in angle brackets, such as <DeviceName>: it stands for any number of children of its type. A MandatoryPlaceholder needs at least one.")),
        ["Abstract type"] = (L.T("Abstract type"), L.T("A type meant only to be derived from. OPC UA creates no instances of it; pick a concrete subtype.")),
        ["Endpoint"] = (L.T("Endpoint"), L.T("Where and how a server is reached: URL, security mode and security policy. A server usually offers several.")),
        ["Security mode"] = (L.T("Security mode"), L.T("None sends everything plainly, Sign protects against tampering, SignAndEncrypt also keeps it secret. Secure in the plugin takes only the last two.")),
        ["Certificate"] = (L.T("Certificate"), L.T("The identity of a server or client. Trusting a server's certificate says: this is the server I mean. The plugin keeps exactly the one you trusted.")),
        ["NodeId"] = (L.T("NodeId"), L.T("The address of a node on a server: its namespace and an identifier (a number, a string, a GUID or bytes). In the document the attribute NodeId (Annex A) holds it.")),
        ["Address space"] = (L.T("Address space"), L.T("All nodes a server offers, reached from its Objects folder along references.")),
        ["ReferenceType"] = (L.T("ReferenceType"), L.T("The kind of link between two nodes, such as HasComponent, HasProperty, Organizes or HasTypeDefinition.")),
        ["Property"] = (L.T("Property"), L.T("A small variable that describes its parent, held by HasProperty: engineering units, a range, a serial number.")),
        ["View"] = (L.T("View"), L.T("A part of the address space a server defines for a purpose, browsed on its own.")),
    };
}
