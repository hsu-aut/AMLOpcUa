// Runs the two chains and hands the results to the analyses. A step that
// fails does not throw: where a chain breaks is itself a result.

using System.Diagnostics;
using System.Xml.Linq;
using Aml.Engine.CAEX;
using OpcUaAml.Export;
using OpcUaAml.Import;
using OpcUaAml.NodeSets;

namespace OpcUaAml.Roundtrip;

public static class RoundtripRunner
{
    public const string ChainA = "UA -> AML (OPC 10000-83 Annex A) -> UA (AML-UA-XSLT rules)";
    public const string ChainB = "AML -> UA (AML-UA-XSLT rules) -> AML (OPC 10000-83 Annex A)";
    public const string ChainC = "UA -> AML (OPC 10000-83 Annex A) -> UA (inverse of Annex A)";

    /// <summary>Chain C for a NodeSet: through Annex A and back through its inverse, compared node by node.</summary>
    public static RoundtripReport UaAmlUaInverse(string nodeSetPath, NodeSetCatalog catalog)
    {
        var watch = Stopwatch.StartNew();
        var subject = Path.GetFileName(nodeSetPath);
        var notes = new List<string>();
        string step = "read the NodeSet";
        try
        {
            var info = NodeSetInfo.Read(nodeSetPath);
            var modelUri = info.PrimaryModel.Model.ModelUri;

            step = "Annex A import (Opc2Aml)";
            var conversion = NodeSetImporter.Convert(nodeSetPath, catalog);
            notes.AddRange(conversion.Warnings.Select(w => "Import: " + w));

            step = "export (inverse of Annex A)";
            var exported = NodeSetExporter.Export(conversion.Document, new NodeSetExportOptions { Mode = ExportMode.AnnexAInverse, NamespaceUri = modelUri });

            step = "analysis";
            var criteria = InverseAnalysis.Analyze(System.Xml.Linq.XDocument.Load(nodeSetPath), conversion.Document.CAEXFile.Node, exported, modelUri);
            return new RoundtripReport(subject, ChainC, criteria, null, null, watch.Elapsed, notes);
        }
        catch (Exception ex)
        {
            return new RoundtripReport(subject, ChainC, Array.Empty<Criterion>(), step, Describe(ex), watch.Elapsed, notes);
        }
    }

    /// <summary>Chain A for a NodeSet; the catalog supplies the models it requires.</summary>
    public static RoundtripReport UaAmlUa(string nodeSetPath, NodeSetCatalog catalog)
    {
        var watch = Stopwatch.StartNew();
        var subject = Path.GetFileName(nodeSetPath);
        var notes = new List<string>();
        string step = "read the NodeSet";
        try
        {
            var info = NodeSetInfo.Read(nodeSetPath);
            var modelUri = info.PrimaryModel.Model.ModelUri;

            step = "Annex A import (Opc2Aml)";
            var conversion = NodeSetImporter.Convert(nodeSetPath, catalog);
            notes.AddRange(conversion.Warnings.Select(w => "Import: " + w));

            step = "export (AML-UA-XSLT rules)";
            var exported = NodeSetExporter.Export(conversion.Document, new NodeSetExportOptions { PublicationDate = new DateTime(2026, 1, 1) });

            step = "analysis";
            var criteria = UaAmlUaAnalysis.Analyze(UaGraph.Load(nodeSetPath), UaGraph.Load(exported), modelUri, RequiredGraphs(info, catalog));
            return new RoundtripReport(subject, ChainA, criteria, null, null, watch.Elapsed, notes);
        }
        catch (Exception ex)
        {
            return new RoundtripReport(subject, ChainA, Array.Empty<Criterion>(), step, Describe(ex), watch.Elapsed, notes);
        }
    }

    /// <summary>
    /// Chain B for an AML document. The catalog must supply the AML base types
    /// NodeSet (http://opcfoundation.org/UA/AML/); the exported file's own
    /// models are added from the export.
    /// </summary>
    public static RoundtripReport AmlUaAml(string amlPath, NodeSetCatalog catalog)
    {
        var watch = Stopwatch.StartNew();
        var subject = Path.GetFileName(amlPath);
        var notes = new List<string>();
        string step = "read the document";
        var work = Directory.CreateTempSubdirectory("opcuaaml-roundtrip-");
        try
        {
            var original = XDocument.Load(amlPath);

            step = "export (AML-UA-XSLT rules)";
            var exported = NodeSetExporter.Export(original, new NodeSetExportOptions { PublicationDate = new DateTime(2026, 1, 1) });
            var nodeSetFile = Path.Combine(work.FullName, Path.GetFileNameWithoutExtension(amlPath) + ".NodeSet2.xml");
            exported.Save(nodeSetFile);

            step = "Annex A import (Opc2Aml)";
            catalog.AddFile(nodeSetFile);
            var conversion = NodeSetImporter.Convert(nodeSetFile, catalog);
            notes.AddRange(conversion.Warnings.Select(w => "Import: " + w));
            var roundtrip = new XDocument(new XElement(conversion.Document.CAEXFile.Node));

            step = "analysis";
            var criteria = AmlUaAmlAnalysis.Analyze(original, roundtrip);
            return new RoundtripReport(subject, ChainB, criteria, null, null, watch.Elapsed, notes);
        }
        catch (Exception ex)
        {
            return new RoundtripReport(subject, ChainB, Array.Empty<Criterion>(), step, Describe(ex), watch.Elapsed, notes);
        }
        finally
        {
            try { work.Delete(true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    /// <summary>The models a NodeSet requires, transitively, without the UA base model.</summary>
    private static IEnumerable<UaGraph> RequiredGraphs(NodeSetInfo root, NodeSetCatalog catalog)
    {
        var seen = new HashSet<string>(root.Models.Select(m => m.Model.ModelUri), StringComparer.Ordinal) { UaGraph.Ua };
        var queue = new Queue<NodeSetInfo>(new[] { root });
        while (queue.Count > 0)
            foreach (var req in queue.Dequeue().Models.SelectMany(m => m.RequiredModels))
                if (seen.Add(req.ModelUri) && catalog.Find(req.ModelUri) is { } provider)
                {
                    queue.Enqueue(provider);
                    yield return UaGraph.Load(provider.FilePath);
                }
    }

    private static string Describe(Exception ex)
    {
        var inner = ex;
        while (inner.InnerException != null && inner is ImportException) inner = inner.InnerException;
        var frame = new StackTrace(inner, true).GetFrames()?.FirstOrDefault(f => f.GetMethod()?.DeclaringType?.Namespace?.StartsWith("MarkdownProcessor") == true);
        var where = frame?.GetMethod() is { } m ? $" (in {m.DeclaringType?.Name}.{m.Name})" : "";
        return $"{inner.GetType().Name}: {inner.Message}{where}";
    }
}
