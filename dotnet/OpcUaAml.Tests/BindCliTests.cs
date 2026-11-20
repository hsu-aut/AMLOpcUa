using Aml.Engine.CAEX;
using Aml.Engine.CAEX.Extensions;
using OpcUaAml.Addressing;
using OpcUaAml.Import;
using OpcUaAml.Server;

namespace OpcUaAml.Tests;

/// <summary>uaaml bind: a model of one's own bound to a server's variables from the command line.</summary>
[Trait("Speed", "Slow")]
public class BindCliTests
{
    private static CAEXDocument LessonModel()
    {
        var doc = CAEXDocument.New_CAEXDocument();
        var station = doc.CAEXFile.InstanceHierarchy.Append("Plant").InternalElement.Append("ProcessingStation");
        station.InternalElement.Append("Drill").InternalElement.Append("SpindleSpeed");
        station.InternalElement.Append("PartsCounter");
        station.InternalElement.Append("Turntable");
        return doc;
    }

    private static async Task<NodeSetServerHost> PracticePlantAsync(string pki) =>
        await NodeSetServerHost.StartAsync(new[] { Path.Combine(Repository.Root, "examples", "mps500", "MPS500.NodeSet2.xml") },
            new NodeSetServerOptions { Port = Uaaml.FreePort(), PkiRoot = Path.Combine(pki, "server") });

    [Fact]
    public async Task Without_apply_the_proposals_are_listed_and_nothing_is_bound()
    {
        using var uaaml = new Uaaml();
        await using var plant = await PracticePlantAsync(uaaml.Folder);
        var doc = uaaml.Save(LessonModel());
        var report = Path.Combine(uaaml.Folder, "binding.csv");

        var run = await uaaml.RunAsync("bind", plant.EndpointUrl, doc, "--report", report, "--insecure", "--accept");

        Assert.Equal(0, run.Exit);
        Assert.Matches(@"Plant/ProcessingStation/Drill/SpindleSpeed\s+->\s+MPS500/ST30_Processing/MD_Drilling_unit/DV_Drill_spindle/Speed\s+(sure|likely)", run.Out);
        Assert.Contains("Plant/ProcessingStation/Turntable", run.Out);
        Assert.Contains("--apply", run.Out);
        Assert.StartsWith("Value of the model;Variable of the server;NodeId;Match", File.ReadAllText(report));
        var unchanged = (InternalElementType)CAEXDocument.LoadFromFile(doc).FindByPath("Plant/ProcessingStation/Drill/SpindleSpeed")!;
        Assert.Null(unchanged.Attribute["NodeId"]);
    }

    /// <summary>
    /// Video round 2026-11-20: against the whole practice plant, PartsCounter was
    /// proposed as likely, and so bound, for the station's RevisionCounter. The DI
    /// folder ParameterSet between the station and PartsProduced counted as words
    /// the model does not have, and as one level more away from the station. The
    /// names stay ambiguous ("counter" against "parts", and ST20_PartsDistributing
    /// makes "parts" a common word), so it is left to the user, not bound.
    /// </summary>
    [Fact]
    public async Task PartsCounter_is_not_bound_to_the_revision_counter_of_the_station()
    {
        using var uaaml = new Uaaml();
        await using var plant = await PracticePlantAsync(uaaml.Folder);
        var doc = uaaml.Save(LessonModel());

        var run = await uaaml.RunAsync("bind", plant.EndpointUrl, doc, "--insecure", "--accept");

        Assert.True(run.Exit == 0, run.Out + run.Error);
        Assert.DoesNotMatch(@"PartsCounter\s+->\s+\S*RevisionCounter\s+(sure|likely)", run.Out);
        Assert.Matches(@"Plant/ProcessingStation/PartsCounter\s+->\s+(MPS500/ST30_Processing/ParameterSet/PartsProduced|\S+\s+check)\s", run.Out);
    }

    [Fact]
    public async Task With_apply_the_sure_and_likely_ones_are_bound_and_saved()
    {
        using var uaaml = new Uaaml();
        await using var plant = await PracticePlantAsync(uaaml.Folder);
        var doc = uaaml.Save(LessonModel());
        var output = Path.Combine(uaaml.Folder, "bound.aml");

        var run = await uaaml.RunAsync("bind", plant.EndpointUrl, doc, "--model", "Plant", "--below", "/MPS500/ST30_Processing", "--apply", "-o", output, "--insecure", "--accept");

        Assert.True(run.Exit == 0, run.Out + run.Error);
        var speed = CAEXDocument.LoadFromFile(output).FindByPath("Plant/ProcessingStation/Drill/SpindleSpeed") as InternalElementType;
        Assert.True(speed != null, "SpindleSpeed not in the saved document. " + run.Out);
        Assert.True(AnnexANodeId.Of(speed) != null, "SpindleSpeed not bound. " + run.Out);
        Assert.Equal("http://hsu-hh.de/UA/MPS500/", AnnexANodeId.Of(speed)!.NamespaceUri);
        Assert.NotNull(speed.Attribute["Value"]);
        Assert.Contains("Bound ", run.Out);
    }

    [Fact]
    public async Task A_document_without_a_model_of_its_own_says_so()
    {
        using var uaaml = new Uaaml();
        await using var plant = await PracticePlantAsync(uaaml.Folder);
        var doc = uaaml.Save(CAEXDocument.New_CAEXDocument());

        var run = await uaaml.RunAsync("bind", plant.EndpointUrl, doc, "--insecure", "--accept");

        Assert.NotEqual(0, run.Exit);
        Assert.Contains("no model of its own", run.Error);
    }
}
