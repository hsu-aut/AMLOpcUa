using OpcUaAml.Addressing;
using OpcUaAml.Server;

namespace OpcUaAml.Tests;

/// <summary>
/// What the address space shows and what a mirror takes: methods are shown,
/// to be called, but never taken into the document.
/// </summary>
public class MirrorFilterTests
{
    private static UaBrowseItem Item(string nodeClass, string ns = "http://example.org/Pump/") =>
        new(new UaNodeAddress(ns, UaIdType.Numeric, "1"), "X", "X", nodeClass, null, "HasComponent");

    [Fact]
    public void The_address_space_shows_methods_a_mirror_does_not_take()
    {
        var filter = new MirrorFilter();

        Assert.True(filter.Shows(Item("Method")));
        Assert.False(filter.Admits(Item("Method")));
        Assert.True(filter.Shows(Item("Variable")));
    }

    [Fact]
    public void Objects_only_and_the_namespaces_hide_methods_too()
    {
        Assert.False(new MirrorFilter { ObjectsOnly = true }.Shows(Item("Method")));
        Assert.True(new MirrorFilter { ObjectsOnly = true }.Shows(Item("Object")));
        Assert.False(new MirrorFilter { Namespaces = new[] { "http://example.org/Other/" } }.Shows(Item("Method")));
    }
}
