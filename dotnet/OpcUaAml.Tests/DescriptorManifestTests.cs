using System.Xml.Linq;
using OpcUaAml.Descriptors;

namespace OpcUaAml.Tests;

public class DescriptorManifestTests
{
    private static readonly XNamespace Fx = "http://opcfoundation.org/UA/FX/2021/08/DescriptorInfo.xsd";

    [Fact]
    public void A_manifest_is_written_as_Annex_J_describes_it()
    {
        var info = new DescriptorInfo("urn:hsu:mps500:station1", new DescriptorVersion(1, 2, 3, 4), "1.00.03");

        var root = info.ToXml().Root!;

        Assert.Equal(Fx + "DescriptorInfo", root.Name);
        Assert.Equal(new[] { "DescriptorIdentifier", "DescriptorVersion", "OpcUaFxVersion" },
            root.Elements().Select(e => e.Name.LocalName));
        Assert.All(root.Descendants(), e => Assert.Equal(Fx, e.Name.Namespace));
        Assert.Equal("urn:hsu:mps500:station1", root.Element(Fx + "DescriptorIdentifier")!.Value);
        Assert.Equal(new[] { "1", "2", "3", "4" }, root.Element(Fx + "DescriptorVersion")!.Elements().Select(e => e.Value));
        Assert.Equal(new[] { "Major", "Minor", "Build", "SubBuild" },
            root.Element(Fx + "DescriptorVersion")!.Elements().Select(e => e.Name.LocalName));
        Assert.Equal("1.00.03", root.Element(Fx + "OpcUaFxVersion")!.Value);
    }

    [Fact]
    public void A_manifest_reads_back()
    {
        var info = new DescriptorInfo("urn:hsu:mps500:station1", new DescriptorVersion(1, 0, 7, 0), "1.00.03");

        var back = DescriptorInfo.Read(info.ToXml(), out var problems);

        Assert.Empty(problems);
        Assert.Equal(info, back);
    }

    [Fact]
    public void The_example_of_the_specification_reads_with_a_note_on_its_namespace()
    {
        // OPC 10000-83 7.3.2, Example 2: without the namespace Annex J declares.
        var example = XDocument.Parse("""
            <DescriptorInfo>
                 <DescriptorIdentifier>urn:company:factory:plant_project:automation_component:1.0.0</DescriptorIdentifier>
                 <DescriptorVersion><Major>1</Major><Minor>0</Minor><Build>0</Build><SubBuild>0</SubBuild></DescriptorVersion>
                 <OpcUaFxVersion>1.0.0</OpcUaFxVersion>
            </DescriptorInfo>
            """);

        var info = DescriptorInfo.Read(example, out var problems);

        Assert.NotNull(info);
        Assert.Equal("urn:company:factory:plant_project:automation_component:1.0.0", info!.Identifier);
        Assert.Equal(new DescriptorVersion(1, 0, 0, 0), info.Version);
        Assert.Contains(problems, p => p.Message.Contains("namespace", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("<DescriptorIdentifier>urn:a</DescriptorIdentifier><OpcUaFxVersion>1</OpcUaFxVersion>", "DescriptorVersion")]
    [InlineData("<DescriptorIdentifier>urn:a</DescriptorIdentifier><DescriptorVersion><Major>1</Major><Minor>0</Minor><Build>0</Build></DescriptorVersion><OpcUaFxVersion>1</OpcUaFxVersion>", "SubBuild")]
    [InlineData("<DescriptorIdentifier>urn:a</DescriptorIdentifier><DescriptorVersion><Major>70000</Major><Minor>0</Minor><Build>0</Build><SubBuild>0</SubBuild></DescriptorVersion><OpcUaFxVersion>1</OpcUaFxVersion>", "Major")]
    [InlineData("<DescriptorIdentifier></DescriptorIdentifier><DescriptorVersion><Major>1</Major><Minor>0</Minor><Build>0</Build><SubBuild>0</SubBuild></DescriptorVersion><OpcUaFxVersion>1</OpcUaFxVersion>", "DescriptorIdentifier")]
    [InlineData("<DescriptorIdentifier>urn:a</DescriptorIdentifier><DescriptorVersion><Major>1</Major><Minor>0</Minor><Build>0</Build><SubBuild>0</SubBuild></DescriptorVersion>", "OpcUaFxVersion")]
    public void A_manifest_without_what_Annex_J_requires_is_named(string content, string missing)
    {
        var doc = XDocument.Parse($"<DescriptorInfo xmlns=\"{Fx}\">{content}</DescriptorInfo>");

        DescriptorInfo.Read(doc, out var problems);

        Assert.Contains(problems, p => p.Message.Contains(missing, StringComparison.Ordinal));
    }

    [Fact]
    public void Another_document_is_not_a_manifest()
    {
        Assert.False(DescriptorInfo.IsManifest(XDocument.Parse("<CAEXFile/>")));
        Assert.True(DescriptorInfo.IsManifest(new DescriptorInfo("urn:a", default, "1").ToXml()));
        Assert.True(DescriptorInfo.IsManifest(XDocument.Parse("<DescriptorInfo/>")));
    }

    [Theory]
    [InlineData("1", 1, 0, 0, 0)]
    [InlineData("1.2", 1, 2, 0, 0)]
    [InlineData("1.2.3.4", 1, 2, 3, 4)]
    public void A_version_is_typed_with_points(string text, int major, int minor, int build, int subBuild)
    {
        Assert.True(DescriptorVersion.TryParse(text, out var version));
        Assert.Equal(new DescriptorVersion((ushort)major, (ushort)minor, (ushort)build, (ushort)subBuild), version);
    }

    [Theory]
    [InlineData("")]
    [InlineData("1.2.3.4.5")]
    [InlineData("1.x")]
    [InlineData("65536")]
    [InlineData("-1")]
    public void A_version_that_is_not_four_numbers_is_refused(string text)
    {
        Assert.False(DescriptorVersion.TryParse(text, out _));
    }
}
