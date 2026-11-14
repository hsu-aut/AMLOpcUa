using Aml.Engine.CAEX;
using OpcUaAml.Import;
using OpcUaAml.NodeSets;

namespace OpcUaAml.Tests;

/// <summary>
/// The NodeSets of the OPC Foundation carry their copyright and licence (MIT)
/// in a comment at the top; the MIT licence asks that the notice go with every
/// copy. The libraries Annex A makes of a NodeSet are such a copy, and they
/// came without it. Each library now carries its NodeSet's notice in CAEX's
/// Copyright.
/// </summary>
public class CopyrightNoticeTests(BundledDiConversion di) : IClassFixture<BundledDiConversion>
{
    private static string? CopyrightOf(CAEXBasicObject library) =>
        library.Node.Elements().FirstOrDefault(e => e.Name.LocalName == "Copyright")?.Value;

    [Fact]
    public void The_libraries_of_a_model_carry_the_notice_of_its_NodeSet()
    {
        var file = di.Result.Document.CAEXFile;
        var diLibraries = new CAEXBasicObject[]
        {
            file.SystemUnitClassLib["SUC_http://opcfoundation.org/UA/DI/"]!,
            file.RoleClassLib["RCL_http://opcfoundation.org/UA/DI/"]!,
            file.InterfaceClassLib["ICL_http://opcfoundation.org/UA/DI/"]!,
        };
        Assert.All(diLibraries, l =>
        {
            var notice = CopyrightOf(l);
            Assert.NotNull(notice);
            Assert.Contains("Copyright (c) 2005-2024 The OPC Foundation", notice);
            Assert.Contains("MIT License", notice);
            // The comment's frame (" * ") is not part of the notice.
            Assert.DoesNotContain("\n *", notice);
        });

        // A model the import brings along (the UA base model) carries its own notice.
        var ua = CopyrightOf(file.SystemUnitClassLib["SUC_http://opcfoundation.org/UA/"]!);
        Assert.NotNull(ua);
        Assert.Contains("Copyright (c) 2005-2026 The OPC Foundation", ua);
    }

    [Fact]
    public void A_NodeSet_without_a_notice_keeps_what_Opc2Aml_writes()
    {
        // Its comment at the top describes the model; it names no copyright or licence.
        var result = NodeSetImporter.Convert(Fixtures.Path("modeldesign", "Mini.NodeSet2.xml"), di.Catalog);

        Assert.Equal("© OPC Foundation", CopyrightOf(result.Document.CAEXFile.SystemUnitClassLib["SUC_http://example.org/Mini/"]!));
    }

    [Fact]
    public void The_practice_plant_carries_the_notice_of_the_university()
    {
        var plant = Path.Combine(Repository.Root, "examples", "mps500", "MPS500.NodeSet2.xml");
        var result = NodeSetImporter.Convert(plant, di.Catalog);

        var notice = CopyrightOf(result.Document.CAEXFile.SystemUnitClassLib["SUC_http://hsu-hh.de/UA/MPS500/"]!);
        Assert.NotNull(notice);
        Assert.Contains("Helmut Schmidt University", notice);
        Assert.Contains("SPDX-License-Identifier: MIT", notice);
        Assert.Contains("Copyright (c) 2005-2024 The OPC Foundation", CopyrightOf(result.Document.CAEXFile.SystemUnitClassLib["SUC_http://opcfoundation.org/UA/DI/"]!));
    }

    [Fact]
    public void The_metamodel_libraries_get_no_notice_of_a_NodeSet()
    {
        // Libraries without OpcUaLibInfo are Annex A's own (the OPC UA meta model), not a NodeSet's.
        var file = di.Result.Document.CAEXFile;
        var meta = file.SystemUnitClassLib.Cast<CAEXBasicObject>().Concat(file.RoleClassLib).Concat(file.InterfaceClassLib).Concat(file.AttributeTypeLib)
            .Where(l => OpcUaLibraryInfo.Read(l) == null).ToList();

        Assert.NotEmpty(meta);
        Assert.All(meta, l => Assert.DoesNotContain("Permission is hereby granted", CopyrightOf(l) ?? ""));
    }

    [Fact]
    public void The_notice_is_read_from_the_comment_at_the_top_of_a_NodeSet()
    {
        var notice = NodeSetInfo.ReadNotice(di.Catalog.Find(Fixtures.DiUri)!.FilePath);

        Assert.NotNull(notice);
        Assert.StartsWith("Copyright (c) 2005-2024 The OPC Foundation, Inc. All rights reserved.", notice);
        Assert.Contains("Permission is hereby granted, free of charge", notice);
    }
}
