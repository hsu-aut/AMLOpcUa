using Aml.Engine.CAEX;
using Aml.Engine.CAEX.Extensions;
using OpcUaAml.Addressing;
using OpcUaAml.Server;

namespace OpcUaAml.Tests;

/// <summary>
/// The bound values of a document as they stand, compared with those of a moment
/// before: what keeping values live needs to follow an element bound after it started.
/// </summary>
public class BindingSetTests
{
    private static readonly UaNodeAddress Timer = new("urn:codesys:GVL", UaIdType.String, "|var|CODESYS Control Win V3.Application.GVL.DO_Timer");
    private static readonly UaNodeAddress Start = new("urn:codesys:GVL", UaIdType.String, "|var|CODESYS Control Win V3.Application.GVL.DO_StartFB");

    private static InternalElementType Bound(InternalElementType parent, string name, UaNodeAddress address)
    {
        var ie = parent.InternalElement.Append(name);
        AnnexANodeId.Write(ie, address);
        ie.Attribute.Append("Value");
        return ie;
    }

    private static (CAEXDocument Doc, InternalElementType Plc, InternalElementType Lamp) Plant()
    {
        var doc = CAEXDocument.New_CAEXDocument();
        var plc = doc.CAEXFile.InstanceHierarchy.Append("OpcUaServer").InternalElement.Append("GVL");
        Bound(plc, "DO_Timer", Timer);
        var lamp = doc.CAEXFile.InstanceHierarchy.Append("Anlagenstruktur").InternalElement.Append("Gerät").InternalElement.Append("Lampe");
        return (doc, plc, lamp);
    }

    [Fact]
    public void An_unchanged_document_has_the_same_bindings()
    {
        var (doc, _, _) = Plant();

        Assert.True(BindingSet.Of(doc).SameAs(BindingSet.Of(doc)));
        Assert.Equal(1, BindingSet.Of(doc).Count);
    }

    [Fact]
    public void An_element_bound_later_is_added()
    {
        var (doc, _, lamp) = Plant();
        var before = BindingSet.Of(doc);

        Bound(lamp, "Blinker", Timer);
        var now = BindingSet.Of(doc);

        Assert.False(now.SameAs(before));
        Assert.Equal(new[] { "Blinker" }, now.AddedSince(before));
        Assert.Empty(now.RemovedSince(before));
    }

    [Fact]
    public void A_copy_of_a_bound_element_is_added()
    {
        var (doc, plc, lamp) = Plant();
        var before = BindingSet.Of(doc);

        // What the editor's Copy and Paste leave: the element again, with its attributes.
        var copy = (InternalElementType)plc.InternalElement["DO_Timer"]!.Copy(deepCopy: true);
        lamp.InternalElement.Insert(copy);
        var now = BindingSet.Of(doc);

        Assert.Equal(new[] { "DO_Timer" }, now.AddedSince(before));
        Assert.Equal(2, now.Count);
    }

    [Fact]
    public void A_deleted_element_is_removed()
    {
        var (doc, plc, _) = Plant();
        var before = BindingSet.Of(doc);

        plc.InternalElement["DO_Timer"]!.Remove();
        var now = BindingSet.Of(doc);

        Assert.Equal(new[] { "DO_Timer" }, now.RemovedSince(before));
        Assert.Equal(0, now.Count);
    }

    [Fact]
    public void An_element_bound_to_another_node_is_removed_and_added()
    {
        var (doc, plc, _) = Plant();
        var before = BindingSet.Of(doc);

        AnnexANodeId.Write(plc.InternalElement["DO_Timer"]!, Start);
        var now = BindingSet.Of(doc);

        Assert.False(now.SameAs(before));
        Assert.Equal(new[] { "DO_Timer" }, now.AddedSince(before));
        Assert.Equal(new[] { "DO_Timer" }, now.RemovedSince(before));
    }

    [Fact]
    public void A_value_written_into_the_document_changes_nothing()
    {
        var (doc, plc, _) = Plant();
        var before = BindingSet.Of(doc);

        ValueSnapshot.SetValue(plc.InternalElement["DO_Timer"]!, new UaReadResult(Timer, true, "Boolean", true, "Good"));

        Assert.True(BindingSet.Of(doc).SameAs(before));
    }

    [Fact]
    public void An_element_without_Value_is_not_bound_yet()
    {
        var (doc, _, lamp) = Plant();
        var before = BindingSet.Of(doc);

        AnnexANodeId.Write(lamp.InternalElement.Append("Blinker"), Timer);

        Assert.True(BindingSet.Of(doc).SameAs(before));
    }
}
