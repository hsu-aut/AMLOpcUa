// The copyright and licence notice of a NodeSet on the libraries Annex A makes
// of it. The NodeSets of the OPC Foundation are under the MIT licence, which
// asks that the notice go with every copy; Opc2Aml leaves it behind, since it
// sits in a comment, and writes "© OPC Foundation" into every library, whoever
// published the model. The notice replaces that in each library whose
// OpcUaLibInfo names the model; without a notice in the NodeSet, Opc2Aml's
// text stays, and any other Copyright is kept.

using Aml.Engine.CAEX;
using OpcUaAml.NodeSets;

namespace OpcUaAml.Import;

internal static class LibraryNotices
{
    /// <summary>What Opc2Aml writes into every library's Copyright (NodeSetToAML.AddLibraryHeaderInfo).</summary>
    internal const string Opc2AmlCopyright = "© OPC Foundation";

    /// <summary>
    /// Gives the libraries of <paramref name="document"/> the notice of the NodeSet
    /// their model comes from: <paramref name="converted"/> itself, or the one
    /// <paramref name="catalog"/> holds for a model it requires.
    /// </summary>
    public static void Add(CAEXDocument document, NodeSetInfo converted, NodeSetCatalog catalog)
    {
        var notices = new Dictionary<string, string?>(StringComparer.Ordinal);
        string? NoticeOf(string modelUri)
        {
            if (notices.TryGetValue(modelUri, out var known)) return known;
            var file = converted.Models.Any(m => m.Model.ModelUri == modelUri) ? converted.FilePath : catalog.Find(modelUri)?.FilePath;
            return notices[modelUri] = file == null ? null : NodeSetInfo.ReadNotice(file);
        }

        var caex = document.CAEXFile;
        var libraries = caex.AttributeTypeLib.Cast<CAEXBasicObject>()
            .Concat(caex.InterfaceClassLib)
            .Concat(caex.RoleClassLib)
            .Concat(caex.SystemUnitClassLib);
        foreach (var library in libraries)
        {
            if (OpcUaLibraryInfo.Read(library) is not { } info) continue;
            var copyright = library.Node.Elements().FirstOrDefault(e => e.Name.LocalName == "Copyright");
            if (copyright is { Value.Length: > 0 } && copyright.Value != Opc2AmlCopyright) continue;
            if (NoticeOf(info.NamespaceUri) is not { } notice) continue;
            // Written to the XML, not through Aml.Engine: with an undo service
            // registered (the editor's), every engine change is recorded.
            if (copyright != null) copyright.Value = notice;
            else InsertCopyright(library.Node, notice);
        }
    }

    /// <summary>A Copyright where CAEX wants it: after Description, Version and Revision.</summary>
    private static void InsertCopyright(System.Xml.Linq.XElement owner, string notice)
    {
        var element = new System.Xml.Linq.XElement(owner.Name.Namespace + "Copyright", notice);
        var before = owner.Elements().LastOrDefault(e => e.Name.LocalName is "Description" or "Version" or "Revision");
        if (before != null) before.AddAfterSelf(element);
        else owner.AddFirst(element);
    }
}
