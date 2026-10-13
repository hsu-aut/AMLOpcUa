using Aml.Engine.CAEX;
using Aml.Engine.CAEX.Extensions;
using Aml.Engine.Services;
using OpcUaAml.Import;

namespace OpcUaAml.Tests;

/// <summary>
/// The AutomationML Editor registers a LookupService for the whole process and
/// turns its automatic reference updates on, for every open document. When an
/// element goes, the service rewrites every reference to it. A merge replaces
/// a library by removing the old one and inserting the new one.
/// </summary>
public class EditorServicesMergeTests(BundledDiConversion di) : IClassFixture<BundledDiConversion>
{
    private const string DeviceType = "SUC_http://opcfoundation.org/UA/DI//DeviceType";

    [Fact]
    public void Replacing_a_library_keeps_the_references_into_it_with_the_editors_lookup_service()
    {
        var target = CAEXDocument.New_CAEXDocument();
        LibraryMerger.Merge(target, di.Result.Document);
        var device = target.CAEXFile.InstanceHierarchy.Append("Plant").InternalElement.Append("Device");
        var path = target.CAEXFile.SystemUnitClassLib["SUC_http://opcfoundation.org/UA/DI/"].SystemUnitClass["DeviceType"].CAEXPath();
        device.RefBaseSystemUnitPath = path;

        var lookup = LookupService.Register();
        try
        {
            // As the editor does it: updates on, the document taking part.
            lookup.IsAutoUpdateEnabled = true;
            // Internal to Aml.Engine; the editor sets it for every document it opens.
            typeof(CAEXDocument).GetProperty("SupportsAutoUpdate", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                .SetValue(target, true);
            lookup.AddDocument(target);
            string[] References() => lookup.ElementsWithCAEXPathReference(target.CAEXFile, path).Select(r => r.ReferenceAttributeValue).ToArray();
            Assert.Single(References());

            var clock = System.Diagnostics.Stopwatch.StartNew();
            LibraryMerger.Merge(target, di.Result.Document);
            Console.Error.WriteLine($"Merge with the editor's lookup service: {clock.Elapsed.TotalSeconds:F1} s");

            Assert.Equal(path, device.RefBaseSystemUnitPath);
            Assert.NotNull(device.SystemUnitClass);
            // The service still knows who refers to the class, now the new one.
            Assert.Single(References());
            // The editor's setting is back as it was.
            Assert.True(lookup.IsAutoUpdateEnabled);
        }
        finally
        {
            LookupService.UnRegister();
        }
    }

    [Fact]
    public void Replacing_libraries_records_a_few_changes_with_the_editors_services()
    {
        var target = CAEXDocument.New_CAEXDocument();
        LibraryMerger.Merge(target, di.Result.Document);

        // The services the editor registers for the whole process.
        var lookup = LookupService.Register();
        var undo = UndoRedoService.Register();
        UniqueNameService.Register();
        try
        {
            lookup.IsAutoUpdateEnabled = true;
            typeof(CAEXDocument).GetProperty("SupportsAutoUpdate", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                .SetValue(target, true);
            lookup.AddDocument(target);
            var recorded = 0;
            undo.PropertyChanged += (_, _) => recorded++;

            var report = LibraryMerger.Merge(target, di.Result.Document);

            // The editor answers each recorded change on its UI thread. Removing
            // and inserting a library are a few; rewriting every reference into
            // it again, under the same path, were thousands.
            Assert.InRange(recorded, 1, 10 * report.Replaced);
            Assert.Equal(GeneratedDiLibraries, target.CAEXFile.SystemUnitClassLib.Select(l => l.Name).Where(LibraryMerger.IsGeneratedLibrary).ToArray());
        }
        finally
        {
            UniqueNameService.UnRegister();
            UndoRedoService.UnRegister();
            LookupService.UnRegister();
        }
    }

    private static readonly string[] GeneratedDiLibraries =
        ["SUC_OpcAmlMetaModel", "SUC_http://opcfoundation.org/UA/", "SUC_http://opcfoundation.org/UA/DI/"];
}
