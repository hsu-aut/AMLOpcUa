using OpcUaAml.Import;
using OpcUaAml.NodeSets;

// Opc2Aml and Aml.Engine are not built for concurrent use, and a conversion
// takes seconds; running the collections one after another is also faster
// than contending for the same work.
[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace OpcUaAml.Tests;

/// <summary>The repository the tests run from, for files beside the code (examples).</summary>
internal static class Repository
{
    public static string Root
    {
        get
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(System.IO.Path.Combine(dir.FullName, "AMLOpcUa.sln"))) dir = dir.Parent;
            return dir?.FullName ?? throw new DirectoryNotFoundException("The repository root was not found above the test assembly.");
        }
    }
}

internal static class Fixtures
{
    public static string Path(params string[] parts) =>
        System.IO.Path.Combine(new[] { AppContext.BaseDirectory, "Fixtures" }.Concat(parts).ToArray());

    public const string UaUri = "http://opcfoundation.org/UA/";
    public const string DiUri = "http://opcfoundation.org/UA/DI/";
}

/// <summary>
/// The bundled DI (1.05.0) converted once for all tests that only read the
/// result. A conversion reads the whole UA base model and takes seconds.
/// </summary>
public sealed class BundledDiConversion
{
    public NodeSetCatalog Catalog { get; } = NodeSetCatalog.Create(Array.Empty<string>());
    public ConversionResult Result { get; }

    public BundledDiConversion()
    {
        var di = Catalog.Find(Fixtures.DiUri) ?? throw new InvalidOperationException("DI is not bundled.");
        Result = NodeSetImporter.Convert(di.FilePath, Catalog);
    }
}
