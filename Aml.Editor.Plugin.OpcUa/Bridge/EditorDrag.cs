// What the AutomationML Editor drags out of its trees. Its trees (Aml.Toolkit's
// AMLTreeView, editor 6.4.3.8) start a drag with
//     new DataObject(typeof(AMLNodeViewModel), node)
// and allow only Move; the view model's CAEXNode is the XML of the node. The
// plugin runs in the editor's process, so the object itself arrives; it is read
// by reflection, so the plugin does not depend on Aml.Toolkit.

using System.Windows;
using System.Xml.Linq;
using Aml.Engine.CAEX;
using Aml.Engine.CAEX.Extensions;

namespace Aml.Editor.Plugin.OpcUa.Bridge;

internal static class EditorDrag
{
    /// <summary>The format of a node dragged from a tree of the editor: the full name of its view model's type.</summary>
    internal const string NodeFormat = "Aml.Toolkit.ViewModel.AMLNodeViewModel";

    /// <summary>Whether the drag comes from a tree of the editor.</summary>
    internal static bool IsEditorNode(IDataObject? data)
    {
        try { return data?.GetDataPresent(NodeFormat) == true; }
        catch (Exception) { return false; }
    }

    /// <summary>
    /// The InternalElement of <paramref name="document"/> dragged from the editor's tree;
    /// null for anything else: a class, an attribute, a node of another document, files, text.
    /// </summary>
    internal static InternalElementType? ElementOf(IDataObject? data, CAEXDocument? document)
    {
        if (document == null || !IsEditorNode(data)) return null;
        object? node;
        try { node = data!.GetData(NodeFormat); }
        catch (Exception) { return null; }
        if (node?.GetType().GetProperty("CAEXNode")?.GetValue(node) is not XElement xml) return null;
        if (xml.Name.LocalName != "InternalElement" || xml.Document == null || !ReferenceEquals(xml.Document, document.CAEXFile.Node.Document)) return null;
        var id = xml.Attribute("ID")?.Value;
        if (string.IsNullOrEmpty(id)) return null;
        return document.FindByID(id, true, null) is InternalElementType element && ReferenceEquals(element.Node, xml) ? element : null;
    }
}
