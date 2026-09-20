using OpcUaAml.Import;
using OpcUaAml.NodeSets;

namespace OpcUaAml.Tests;

[Trait("Speed", "Slow")]
public class ConversionCacheTests
{
    private static readonly string Safety = Fixtures.Path("uafx", "Opc.Ua.Safety.NodeSet2.xml");

    [Fact]
    public void A_second_conversion_of_the_same_files_comes_from_the_cache()
    {
        var folder = Directory.CreateTempSubdirectory("opcuaaml-cache-test-").FullName;
        var previous = NodeSetImporter.Cache;
        try
        {
            NodeSetImporter.Cache = new ConversionCache(folder);
            var catalog = NodeSetCatalog.Create(Array.Empty<string>());
            var first = NodeSetImporter.Convert(Safety, catalog);
            var second = NodeSetImporter.Convert(Safety, catalog);

            Assert.False(first.FromCache);
            Assert.True(second.FromCache);
            Assert.Equal(first.LoadedModels, second.LoadedModels);
            Assert.Equal(first.Warnings, second.Warnings);
            Assert.Equal(
                first.Document.CAEXFile.SystemUnitClassLib.Select(l => $"{l.Name}:{l.SystemUnitClass.Count}"),
                second.Document.CAEXFile.SystemUnitClassLib.Select(l => $"{l.Name}:{l.SystemUnitClass.Count}"));
            Assert.True(second.Duration < first.Duration);
        }
        finally
        {
            NodeSetImporter.Cache = previous;
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void A_damaged_entry_is_converted_again()
    {
        var folder = Directory.CreateTempSubdirectory("opcuaaml-cache-damaged-").FullName;
        var previous = NodeSetImporter.Cache;
        try
        {
            NodeSetImporter.Cache = new ConversionCache(folder);
            var catalog = NodeSetCatalog.Create(Array.Empty<string>());
            NodeSetImporter.Convert(Safety, catalog);
            // Half a file, as a crash while copying would leave it.
            foreach (var aml in Directory.GetFiles(folder, "*.aml"))
            {
                var bytes = File.ReadAllBytes(aml);
                File.WriteAllBytes(aml, bytes[..(bytes.Length / 2)]);
            }

            var again = NodeSetImporter.Convert(Safety, catalog);

            Assert.False(again.FromCache);
            Assert.NotEmpty(again.Document.CAEXFile.SystemUnitClassLib);
            Assert.True(NodeSetImporter.Convert(Safety, catalog).FromCache);
        }
        finally
        {
            NodeSetImporter.Cache = previous;
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void Conversions_on_several_threads_do_not_disturb_each_other()
    {
        // Aml.Engine keeps static state while Opc2Aml runs; conversions and
        // document loads share one lock. Without it this failed with
        // "Collection was modified".
        var folder = Directory.CreateTempSubdirectory("opcuaaml-cache-parallel-").FullName;
        var previous = NodeSetImporter.Cache;
        try
        {
            NodeSetImporter.Cache = new ConversionCache(folder);
            var catalog = NodeSetCatalog.Create(new[] { Fixtures.Path("uafx") });
            var files = new[] { Safety, Fixtures.Path("uafx", "opc.ua.fx.data.nodeset2.xml"), Safety };
            var results = new ConversionResult[files.Length];
            Parallel.For(0, files.Length, i => results[i] = NodeSetImporter.Convert(files[i], catalog));

            Assert.All(results, r => Assert.NotEmpty(r.Document.CAEXFile.SystemUnitClassLib));
        }
        finally
        {
            NodeSetImporter.Cache = previous;
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void A_changed_file_gets_another_key()
    {
        var folder = Directory.CreateTempSubdirectory("opcuaaml-cache-key-").FullName;
        try
        {
            var copy = Path.Combine(folder, "Safety.NodeSet2.xml");
            File.Copy(Safety, copy);
            var catalog = NodeSetCatalog.Create(Array.Empty<string>());
            var before = ConversionCache.Key(NodeSetInfo.Read(copy), catalog);
            Assert.Equal(before, ConversionCache.Key(NodeSetInfo.Read(copy), catalog));

            File.AppendAllText(copy, "<!-- changed -->");
            Assert.NotEqual(before, ConversionCache.Key(NodeSetInfo.Read(copy), catalog));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }
}
