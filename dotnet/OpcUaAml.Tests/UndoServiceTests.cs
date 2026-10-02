using Aml.Engine.Services;
using OpcUaAml.Import;
using OpcUaAml.NodeSets;

namespace OpcUaAml.Tests;

/// <summary>
/// The AutomationML Editor registers an UndoRedoService for the whole process
/// and answers every change it records with a call on its UI thread. A
/// conversion works on documents of its own; recording their changes made an
/// import inside the editor take minutes instead of seconds.
/// </summary>
[Trait("Speed", "Slow")]
public class UndoServiceTests
{
    [Fact]
    public void A_conversion_records_nothing_with_an_undo_service_registered()
    {
        var folder = Directory.CreateTempSubdirectory("opcuaaml-undo-test-").FullName;
        var previous = NodeSetImporter.Cache;
        var service = UndoRedoService.Register();
        var recorded = 0;
        service.PropertyChanged += (_, _) => recorded++;
        try
        {
            // An empty cache, so the NodeSet is converted and not read back.
            NodeSetImporter.Cache = new ConversionCache(folder);
            var result = NodeSetImporter.Convert(Fixtures.Path("modeldesign", "Mini.NodeSet2.xml"), NodeSetCatalog.Create(Array.Empty<string>()));

            Assert.False(result.FromCache);
            Assert.Equal(0, recorded);
        }
        finally
        {
            UndoRedoService.UnRegister();
            NodeSetImporter.Cache = previous;
            Directory.Delete(folder, recursive: true);
        }
    }
}
