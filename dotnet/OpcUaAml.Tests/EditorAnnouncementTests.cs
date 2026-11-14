using System.Xml.Linq;
using Aml.Engine.CAEX;
using Aml.Engine.CAEX.Commands;
using OpcUaAml.Links;

namespace OpcUaAml.Tests;

/// <summary>
/// What the core hangs into a document by XLinq it announces as Aml.Engine
/// announces an element appended through its API: the editor's trees follow
/// those events, not the XML.
/// </summary>
public class EditorAnnouncementTests
{
    [Fact]
    public void The_object_references_library_is_announced_to_the_editor()
    {
        var doc = CAEXDocument.New_CAEXDocument();
        var heard = new List<XElement>();
        void Heard(object? sender, CAEXElementChangeEventArgs e)
        {
            if (e.ChangeMode.HasFlag(CAEXElementChangeMode.Added) && e.CAEXElement?.Document == doc.XDocument)
                lock (heard) heard.Add(e.CAEXElement);
        }
        CaexCommand.CAEXElementChangedEvent += Heard;
        try { ObjectReferences.EnsureLibrary(doc.CAEXFile); }
        finally { CaexCommand.CAEXElementChangedEvent -= Heard; }

        Assert.Contains(doc.CAEXFile.AttributeTypeLib[ObjectReferences.Lib]!.Node, heard);
    }
}
