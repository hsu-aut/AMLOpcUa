using System.Xml.Linq;
using Aml.Engine.CAEX;
using OpcUaAml.Import;

namespace OpcUaAml.Tests;

/// <summary>
/// Which AutomationML edition a document is written for (Rainer Drath,
/// 2026-11-20: the beta of the editor for Edition 3 references by ID, and
/// importing the types into such a document gave 150796 violations). The
/// plugin works with Edition 2 only: CAEX 3.0 with SuperiorStandardVersion
/// "AutomationML 2.x".
/// </summary>
public class AmlEditionsTests
{
    private static CAEXDocument Caex3(params string[] standards)
    {
        var doc = CAEXDocument.New_CAEXDocument(CAEXDocument.CAEXSchema.CAEX3_0);
        var file = doc.CAEXFile.Node;
        foreach (var old in file.Elements().Where(e => e.Name.LocalName == "SuperiorStandardVersion").ToList()) old.Remove();
        foreach (var s in standards.Reverse())
            file.AddFirst(new XElement(file.Name.Namespace + "SuperiorStandardVersion", s));
        return doc;
    }

    [Theory]
    [InlineData("AutomationML 2.10")]   // the AutomationML Editor's spelling
    [InlineData("AutomationML 2.1")]    // Aml.Engine's for a new document
    [InlineData(" AutomationML 2.10 ")]
    [InlineData("automationml 2.10")]
    public void CAEX_3_0_with_AutomationML_2_is_Edition_2(string standard)
    {
        var edition = AmlEditions.Of(Caex3(standard));

        Assert.Equal(AmlEdition.Edition2, edition.Edition);
        Assert.True(edition.Supported);
        Assert.Equal([standard.Trim()], edition.StandardVersions);
    }

    [Fact]
    public void A_new_document_of_the_engine_is_Edition_2()
    {
        var edition = AmlEditions.Of(CAEXDocument.New_CAEXDocument(CAEXDocument.CAEXSchema.CAEX3_0));

        Assert.Equal(AmlEdition.Edition2, edition.Edition);
    }

    [Fact]
    public void CAEX_3_0_without_a_SuperiorStandardVersion_counts_as_Edition_2()
    {
        // Written by tools that leave it out; CAEX 3.0 without it is Edition 2's schema.
        var edition = AmlEditions.Of(Caex3());

        Assert.Equal(AmlEdition.Edition2, edition.Edition);
        Assert.Empty(edition.StandardVersions);
    }

    [Theory]
    [InlineData("AutomationML 3.0")]
    [InlineData("AutomationML 3")]
    [InlineData("AutomationML 4.1")]
    public void Another_major_version_of_AutomationML_is_not_supported(string standard)
    {
        var edition = AmlEditions.Of(Caex3(standard));

        Assert.Equal(AmlEdition.Other, edition.Edition);
        Assert.False(edition.Supported);
    }

    [Fact]
    public void One_SuperiorStandardVersion_of_another_edition_is_enough_to_refuse()
    {
        var edition = AmlEditions.Of(Caex3("AutomationML 2.10", "AutomationML 3.0"));

        Assert.Equal(AmlEdition.Other, edition.Edition);
    }

    [Fact]
    public void A_standard_other_than_AutomationML_beside_it_does_not_count()
    {
        var edition = AmlEditions.Of(Caex3("AutomationML 2.10", "VDI/VDE 3682 2015"));

        Assert.Equal(AmlEdition.Edition2, edition.Edition);
    }

    [Fact]
    public void CAEX_2_15_is_Edition_1()
    {
        var edition = AmlEditions.Of(CAEXDocument.New_CAEXDocument(CAEXDocument.CAEXSchema.CAEX2_15));

        Assert.Equal(AmlEdition.Edition1, edition.Edition);
        Assert.False(edition.Supported);
        Assert.Equal("2.15", edition.CaexVersion);
    }

    [Fact]
    public void Importing_into_another_edition_is_refused_and_says_why()
    {
        var target = Caex3("AutomationML 3.0");
        var source = CAEXDocument.New_CAEXDocument(CAEXDocument.CAEXSchema.CAEX3_0);

        var ex = Assert.Throws<ImportException>(() => LibraryMerger.Merge(target, source));

        Assert.Contains("AutomationML 3.0", ex.Message);
        Assert.Contains("Edition 2", ex.Message);
    }

    [Fact]
    public void Require_lets_Edition_2_pass_and_refuses_another()
    {
        AmlEditions.Require(Caex3("AutomationML 2.10"));

        var ex = Assert.Throws<ImportException>(() => AmlEditions.Require(Caex3("AutomationML 3.0")));
        Assert.Contains("Edition 2", ex.Message);
    }
}
