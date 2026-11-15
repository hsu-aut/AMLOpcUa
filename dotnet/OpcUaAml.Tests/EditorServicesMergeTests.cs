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
        // Made before the services: its own changes are not the merge's.
        var changed = Conversions.Changed(di.Result.Document);

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

            var report = LibraryMerger.Merge(target, changed);

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

    /// <summary>
    /// The lookup service builds its tables for a document again the next time it
    /// is asked, and with automatic updates on, it reports every reference it finds
    /// as updated. The editor's tree searches itself once for each report: after an
    /// import or after taking the MPS 500 the editor did not answer for minutes.
    /// </summary>
    [Fact]
    public void After_a_merge_the_editors_lookup_service_reports_no_reference_as_updated()
    {
        var target = CAEXDocument.New_CAEXDocument();
        var lookup = LookupService.Register();
        try
        {
            lookup.IsAutoUpdateEnabled = true;
            typeof(CAEXDocument).GetProperty("SupportsAutoUpdate", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                .SetValue(target, true);
            lookup.AddDocument(target);
            var reported = 0;
            lookup.ReferenceUpdated += (_, _) => reported++;

            LibraryMerger.Merge(target, di.Result.Document);
            // The editor asks right after, for its trees and views.
            _ = lookup.AllClassReferences(target, "[SUC_http://opcfoundation.org/UA/DI/]/[DeviceType]").ToList();

            Assert.True(reported < 10, $"{reported} reference(s) reported as updated");
            Assert.True(lookup.IsAutoUpdateEnabled);
        }
        finally
        {
            LookupService.UnRegister();
        }
    }

    /// <summary>
    /// The same with instances in the document that use the classes of its
    /// libraries, as after taking the practice plant's typed nodes: the editor's
    /// tree searches itself once for each reference the lookup service reports.
    /// </summary>
    [Fact]
    public void A_merge_beside_typed_instances_reports_no_reference_as_updated()
    {
        var target = CAEXDocument.New_CAEXDocument();
        LibraryMerger.Merge(target, di.Result.Document);
        var path = target.CAEXFile.SystemUnitClassLib["SUC_http://opcfoundation.org/UA/DI/"].SystemUnitClass["DeviceType"].CAEXPath();
        var plant = target.CAEXFile.InstanceHierarchy.Append("Plant");
        for (var i = 0; i < 50; i++) plant.InternalElement.Append($"Device{i}").RefBaseSystemUnitPath = path;
        var model = NodeSetImporter.Convert(Path.Combine(Repository.Root, "examples", "mps500", "MPS500.NodeSet2.xml"), di.Catalog);
        var lookup = LookupService.Register();
        try
        {
            lookup.IsAutoUpdateEnabled = true;
            typeof(CAEXDocument).GetProperty("SupportsAutoUpdate", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                .SetValue(target, true);
            lookup.AddDocument(target);
            var reported = 0;
            lookup.ReferenceUpdated += (_, _) => reported++;

            LibraryMerger.Merge(target, model.Document);
            // The editor asks right after, for its trees and views.
            _ = lookup.AllClassReferences(target, path).ToList();

            Assert.True(reported < 10, $"{reported} reference(s) reported as updated");
            // The 50 instances and the MPS 500's StationType, a DeviceType; none twice.
            Assert.Equal(51, lookup.ElementsWithCAEXPathReference(target.CAEXFile, path).Count());
        }
        finally
        {
            LookupService.UnRegister();
        }
    }

    /// <summary>
    /// Taking nodes hangs typed elements into the document and then tells the
    /// editor's trees of them (Announce). With automatic updates on, the lookup
    /// service took each announcement as news and reported every reference of
    /// the new elements as updated; the editor's tree searched itself once for
    /// each. After taking the practice plant's 188 typed nodes the editor did
    /// not answer for minutes. The service's tables are complete by then.
    /// </summary>
    [Fact]
    public void Announcing_taken_elements_reports_no_reference_as_updated()
    {
        var target = CAEXDocument.New_CAEXDocument();
        LibraryMerger.Merge(target, di.Result.Document);
        var path = target.CAEXFile.SystemUnitClassLib["SUC_http://opcfoundation.org/UA/DI/"].SystemUnitClass["DeviceType"].CAEXPath();
        var plant = target.CAEXFile.InstanceHierarchy.Append("Plant");
        var lookup = LookupService.Register();
        try
        {
            lookup.IsAutoUpdateEnabled = true;
            typeof(CAEXDocument).GetProperty("SupportsAutoUpdate", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                .SetValue(target, true);
            lookup.AddDocument(target);
            var reported = 0;
            lookup.ReferenceUpdated += (_, _) => reported++;
            var added = new List<CAEXBasicObject>();
            // As AddressSpaceMirror does it: built quietly, then announced.
            EditorServices.Quietly(target, () =>
            {
                for (var i = 0; i < 50; i++)
                {
                    var device = plant.InternalElement.Append($"Device{i}");
                    device.RefBaseSystemUnitPath = path;
                    added.Add(device);
                }
            });

            EditorServices.Announce(target, added);

            Assert.True(reported < 10, $"{reported} reference(s) reported as updated");
            // The service still knows the new references, and updates stay on.
            Assert.Equal(50, lookup.ElementsWithCAEXPathReference(target.CAEXFile, path).Count());
            Assert.True(lookup.IsAutoUpdateEnabled);
        }
        finally
        {
            LookupService.UnRegister();
        }
    }

    /// <summary>
    /// A value written for a variable that has no Value attribute yet adds one.
    /// When the lookup service holds no tables for the document at that moment,
    /// it builds them, and with automatic updates on it reported every reference
    /// of the document as updated: 310 000 reports in the editor while the
    /// values of the practice plant's 188 taken nodes were written.
    /// </summary>
    [Fact]
    public void Writing_a_first_value_reports_no_reference_as_updated()
    {
        var target = CAEXDocument.New_CAEXDocument();
        LibraryMerger.Merge(target, di.Result.Document);
        var element = target.CAEXFile.InstanceHierarchy.Append("Plant").InternalElement.Append("Speed");
        var lookup = LookupService.Register();
        try
        {
            lookup.IsAutoUpdateEnabled = true;
            typeof(CAEXDocument).GetProperty("SupportsAutoUpdate", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                .SetValue(target, true);
            // The editor's service without tables for the document, as seen after taking nodes.
            lookup.RemoveDocument(target);
            var reported = 0;
            lookup.ReferenceUpdated += (_, _) => reported++;

            EditorServices.WithoutUndo(target, () => element.Attribute.Append("Value").Value = "12.5");

            Assert.True(reported < 10, $"{reported} reference(s) reported as updated");
            Assert.True(lookup.IsAutoUpdateEnabled);
        }
        finally
        {
            LookupService.UnRegister();
        }
    }

    /// <summary>
    /// Values change no reference. Written with the lookup service held, each
    /// batch of live values made the service build its tables for the whole
    /// document again: with the practice plant the editor wrote about twelve
    /// values a second instead of twenty-four, and hardly answered.
    /// </summary>
    [Fact]
    public void Writing_values_leaves_the_lookup_tables_as_they_are_and_records_no_undo()
    {
        var target = CAEXDocument.New_CAEXDocument();
        LibraryMerger.Merge(target, di.Result.Document);
        var element = target.CAEXFile.InstanceHierarchy.Append("Plant").InternalElement.Append("Device");
        var value = element.Attribute.Append("Value");
        var lookup = LookupService.Register();
        var undo = UndoRedoService.Register();
        try
        {
            lookup.IsAutoUpdateEnabled = true;
            lookup.AddDocument(target);
            var recorded = 0;
            undo.PropertyChanged += (_, _) => recorded++;
            var tables = Tables(lookup, target);

            for (var i = 0; i < 5; i++) EditorServices.WithoutUndo(target, () => value.Value = i.ToString());

            Assert.Equal("4", value.Value);
            Assert.Equal(0, recorded);
            // The same tables: nothing was built again.
            Assert.Same(tables, Tables(lookup, target));
        }
        finally
        {
            UndoRedoService.UnRegister();
            LookupService.UnRegister();
        }
    }

    /// <summary>The lookup tables the service holds for a document (internal to Aml.Engine.Services).</summary>
    private static object? Tables(LookupService lookup, CAEXDocument document)
    {
        var dictionary = typeof(LookupService).GetField("_caexElementDictionary", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(lookup)!;
        var get = dictionary.GetType().GetMethods().First(m => m.Name == "GetOrLoadValue");
        var args = get.GetParameters().Length == 2 ? new object[] { document.XDocument, true } : new object[] { document.XDocument };
        return get.Invoke(dictionary, args);
    }

    private static readonly string[] GeneratedDiLibraries =
        ["SUC_OpcAmlMetaModel", "SUC_http://opcfoundation.org/UA/", "SUC_http://opcfoundation.org/UA/DI/"];
}
