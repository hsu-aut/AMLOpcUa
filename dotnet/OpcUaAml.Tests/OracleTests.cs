using OpcUaAml.Compare;
using OpcUaAml.Import;
using OpcUaAml.NodeSets;
using Xunit.Abstractions;

namespace OpcUaAml.Tests;

/// <summary>
/// Our import against the AML libraries the OPC Foundation publishes for UAFX
/// (UA-Nodeset tag UAFX-1.00.04-2026-07-22), generated from the same inputs:
/// UA 1.05.05 and DI 1.04.0, as the libraries themselves record.
/// </summary>
/// <remarks>
/// The published libraries come from an older Opc2Aml (writer version
/// 1.2.9336, July 2025) than the one vendored here (January 2026). The two
/// agree on the class skeleton, which is what this test pins down; attribute
/// details differ by generator version and are only reported, see
/// docs/import.md.
/// </remarks>
public sealed class OracleConversion
{
    public Aml.Engine.CAEX.CAEXDocument Ours { get; }
    public Aml.Engine.CAEX.CAEXDocument Theirs { get; }

    public OracleConversion()
    {
        var catalog = NodeSetCatalog.Create(new[] { Fixtures.Path("oracle-inputs") }, includeBundled: false);
        Ours = NodeSetImporter.Convert(Fixtures.Path("oracle-inputs", "Opc.Ua.Di.NodeSet2.xml"), catalog).Document;
        Theirs = NodeSetImporter.ReadContainer(Fixtures.Path("uafx", "Opc.Ua.Di.NodeSet2.xml.amlx"));
    }
}

public class OracleTests(OracleConversion oracle, ITestOutputHelper output) : IClassFixture<OracleConversion>
{
    /// <summary>
    /// The newer generator gives OptionSet attribute types their integer data
    /// type; the older one left it empty. These 20 types are the only
    /// skeleton-level difference.
    /// </summary>
    private static readonly string[] OptionSetTypes =
    {
        "AccessLevelExType", "AccessLevelType", "AccessRestrictionType", "AlarmMask", "AttributeWriteMask",
        "DataSetFieldContentMask", "DataSetFieldFlags", "EventNotifierType", "JsonDataSetMessageContentMask",
        "JsonNetworkMessageContentMask", "LldpSystemCapabilitiesMap", "LogRecordMask", "PasswordOptionsMask",
        "PermissionType", "PubSubConfigurationRefMask", "TrustListValidationOptions",
        "UadpDataSetMessageContentMask", "UadpNetworkMessageContentMask", "UserConfigurationMask", "UpdateBehavior",
    };

    [Fact]
    public void DI_class_skeleton_matches_the_published_library()
    {
        var ours = oracle.Ours;
        var theirs = oracle.Theirs;

        var skeleton = new LibraryComparer { CompareFacets = false }.Compare(ours, theirs);
        foreach (var d in skeleton) output.WriteLine(d.ToString());

        var full = new LibraryComparer().Compare(ours, theirs);
        output.WriteLine($"Attribute-level differences (generator version): {full.Count}");
        foreach (var (key, count, example) in LibraryComparer.Summarize(full).Take(25))
            output.WriteLine($"{count,6} {key}   e.g. {example}");

        var unexpected = skeleton.Where(d => !(d.Kind == DifferenceKind.Changed
            && d.Path.StartsWith("ATL:", StringComparison.Ordinal)
            && OptionSetTypes.Any(t => d.Path.EndsWith("/" + t, StringComparison.Ordinal))
            && d.Detail.Contains("type=xs:unsigned"))).ToList();
        Assert.Empty(unexpected);
        Assert.Equal(OptionSetTypes.Length, skeleton.Count);
    }

    /// <summary>
    /// IDs encode the UA NodeId ("nsu=...;i=...", URL-encoded). The published
    /// libraries were generated with the OPC UA stack 1.5.375; ours use 1.5.378,
    /// for which patch 0002 rewrote the ID formatting. Same IDs prove the
    /// rewrite changed nothing a later import or a reference could notice.
    /// </summary>
    [Fact]
    public void Class_IDs_are_identical_to_the_published_library()
    {
        static Dictionary<string, string> Ids(Aml.Engine.CAEX.CAEXDocument doc)
        {
            var ids = new Dictionary<string, string>();
            void Walk(string path, IEnumerable<Aml.Engine.CAEX.CAEXObject> items, Func<Aml.Engine.CAEX.CAEXObject, IEnumerable<Aml.Engine.CAEX.CAEXObject>> children)
            {
                foreach (var o in items)
                {
                    var p = path + "/" + o.Name;
                    if (!string.IsNullOrEmpty(o.ID)) ids[p] = o.ID;
                    Walk(p, children(o), children);
                }
            }
            foreach (var lib in doc.CAEXFile.SystemUnitClassLib)
                Walk("SUC:" + lib.Name, lib.SystemUnitClass, o =>
                    (o is Aml.Engine.CAEX.SystemUnitFamilyType f ? f.SystemUnitClass.Cast<Aml.Engine.CAEX.CAEXObject>() : Enumerable.Empty<Aml.Engine.CAEX.CAEXObject>())
                    .Concat(((Aml.Engine.CAEX.SystemUnitClassType)o).InternalElement));
            foreach (var lib in doc.CAEXFile.InterfaceClassLib)
                Walk("ICL:" + lib.Name, lib.InterfaceClass, o => ((Aml.Engine.CAEX.InterfaceFamilyType)o).InterfaceClass);
            foreach (var lib in doc.CAEXFile.RoleClassLib)
                Walk("RCL:" + lib.Name, lib.RoleClass, o => ((Aml.Engine.CAEX.RoleFamilyType)o).RoleClass);
            return ids;
        }

        var ours = Ids(oracle.Ours);
        var theirs = Ids(oracle.Theirs);
        var shared = ours.Keys.Intersect(theirs.Keys).ToList();
        var mismatched = shared.Where(k => ours[k] != theirs[k]).ToList();
        foreach (var k in mismatched.Take(20)) output.WriteLine($"{k}: {ours[k]} vs {theirs[k]}");

        Assert.True(shared.Count > 1000, $"only {shared.Count} shared IDs");
        Assert.Contains(shared, k => theirs[k].Contains("nsu%3Dhttp%3A%2F%2Fopcfoundation.org%2FUA%2FDI%2F"));
        Assert.Empty(mismatched);
    }
}
