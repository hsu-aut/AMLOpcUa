// Which AutomationML edition a document is written for, so the plugin and the
// command line work with the one they were made for and refuse the others,
// saying why (Rainer Drath, 2026-11-20: the beta of the editor for Edition 3
// references by ID, and importing the OPC UA types into such a document gave
// 150796 violations).
//
//   Edition 1  CAEX 2.15
//   Edition 2  CAEX 3.0, SuperiorStandardVersion "AutomationML 2.x": the
//              AutomationML Editor writes "AutomationML 2.10", Aml.Engine
//              "AutomationML 2.1" for a new document
//   Other      anything else, Edition 3 among it
//
// A CAEX 3.0 document without a SuperiorStandardVersion counts as Edition 2:
// it is that edition's schema, and some tools leave the element out. One
// SuperiorStandardVersion of another AutomationML major version is enough to
// refuse; standards other than AutomationML beside it do not count.
// docs/aml-editions.md tells plugin authors how to do the same.

using System.Text.RegularExpressions;
using Aml.Engine.CAEX;

namespace OpcUaAml.Import;

public enum AmlEdition
{
    Edition1,
    Edition2,
    Other,
}

/// <param name="CaexVersion">The CAEX SchemaVersion of the file, such as "3.0".</param>
/// <param name="StandardVersions">Its SuperiorStandardVersion elements, trimmed, in their order.</param>
public sealed record AmlEditionInfo(AmlEdition Edition, string CaexVersion, IReadOnlyList<string> StandardVersions)
{
    /// <summary>The edition this plugin works with.</summary>
    public bool Supported => Edition == AmlEdition.Edition2;

    /// <summary>The SuperiorStandardVersion that names the edition: the first of another major version, else the first.</summary>
    public string? Standard => StandardVersions.FirstOrDefault(s => AmlEditions.MajorOf(s) is { } m && m != 2) ?? StandardVersions.FirstOrDefault();

    /// <summary>For a log or an error, in English: "AutomationML Edition 2 (AutomationML 2.10)".</summary>
    public string Describe() => Edition switch
    {
        AmlEdition.Edition1 => $"AutomationML Edition 1 (CAEX {CaexVersion})",
        AmlEdition.Edition2 => "AutomationML Edition 2" + (Standard != null ? $" ({Standard})" : $" (CAEX {CaexVersion})"),
        _ => Standard != null ? $"{Standard} (CAEX {CaexVersion})" : $"CAEX {CaexVersion}",
    };
}

public static class AmlEditions
{
    private static readonly Regex AutomationML = new(@"^AutomationML\s+(\d+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>The edition <paramref name="document"/> is written for.</summary>
    public static AmlEditionInfo Of(CAEXDocument document)
    {
        var file = document.CAEXFile;
        var caex = file.SchemaVersion ?? "";
        var standards = file.Node.Elements()
            .Where(e => e.Name.LocalName == "SuperiorStandardVersion")
            .Select(e => e.Value.Trim())
            .Where(s => s.Length > 0)
            .ToList();
        var edition = caex switch
        {
            "2.15" => AmlEdition.Edition1,
            "3.0" when standards.All(s => MajorOf(s) is null or 2) => AmlEdition.Edition2,
            _ => AmlEdition.Other,
        };
        return new AmlEditionInfo(edition, caex, standards);
    }

    /// <summary>The major version an "AutomationML x.y" names; null for another standard.</summary>
    internal static int? MajorOf(string standard)
    {
        var m = AutomationML.Match(standard.Trim());
        return m.Success && int.TryParse(m.Groups[1].Value, out var major) ? major : null;
    }

    /// <exception cref="ImportException">The document is not written for AutomationML Edition 2.</exception>
    public static void Require(CAEXDocument document)
    {
        var edition = Of(document);
        if (edition.Supported) return;
        throw new ImportException(edition.Edition == AmlEdition.Edition1
            ? $"The document uses CAEX {edition.CaexVersion} (AutomationML Edition 1). The OPC UA libraries (OPC 10000-83 Annex A) need CAEX 3.0 " +
              "(AutomationML Edition 2, AutomationML 2.10). Convert the document first, e.g. with the AutomationML Editor."
            : $"The document is written for {edition.Describe()}. AMLOpcUa works with AutomationML Edition 2 " +
              "(CAEX 3.0, AutomationML 2.10) only; it neither reads nor changes documents of another edition.");
    }
}
