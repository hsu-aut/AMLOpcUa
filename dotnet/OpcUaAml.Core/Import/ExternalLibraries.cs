// The libraries a document gets through its ExternalReferences: each
// referenced file that can be found is read once, and every library in it is
// listed with the alias that addresses it and the paths of its classes.
// A file that is missing or does not read is left out; a reference to it
// then counts as absent, so the import brings its own copy.

using System.Xml.Linq;
using Aml.Engine.CAEX;

namespace OpcUaAml.Import;

public static class ExternalLibraries
{
    public sealed record Library(string Alias, string Path, string? Version, IReadOnlySet<string> ClassPaths);

    private static readonly HashSet<string> LibraryElements = new(StringComparer.Ordinal)
    {
        "AttributeTypeLib", "InterfaceClassLib", "RoleClassLib", "SystemUnitClassLib",
    };

    private static readonly HashSet<string> ClassElements = new(StringComparer.Ordinal)
    {
        "AttributeType", "InterfaceClass", "RoleClass", "SystemUnitClass",
    };

    /// <summary>Library name to where it comes from; the first reference that provides a name wins.</summary>
    public static IReadOnlyDictionary<string, Library> Read(CAEXDocument document, string? folder)
    {
        var libraries = new Dictionary<string, Library>(StringComparer.Ordinal);
        foreach (var reference in document.CAEXFile.ExternalReference)
        {
            var file = Locate(reference.Path, folder);
            if (file == null || string.IsNullOrEmpty(reference.Alias)) continue;
            XElement root;
            try
            {
                root = XDocument.Load(file).Root!;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Xml.XmlException)
            {
                continue;
            }
            foreach (var lib in root.Elements().Where(e => LibraryElements.Contains(e.Name.LocalName)))
            {
                var name = (string?)lib.Attribute("Name");
                if (string.IsNullOrEmpty(name) || libraries.ContainsKey(name)) continue;
                var version = lib.Elements().FirstOrDefault(e => e.Name.LocalName == "Version")?.Value;
                libraries[name] = new Library(reference.Alias, reference.Path, version, ClassPaths(lib, name));
            }
        }
        return libraries;
    }

    /// <summary>The file a reference names, relative to the document's folder unless absolute.</summary>
    private static string? Locate(string? path, string? folder)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        if (Uri.TryCreate(path, UriKind.Absolute, out var uri) && uri.IsFile) path = uri.LocalPath;
        var file = System.IO.Path.IsPathRooted(path) ? path : folder != null ? System.IO.Path.Combine(folder, path) : null;
        return file != null && File.Exists(file) ? file : null;
    }

    /// <summary>"Library/Class/Nested" for every class and attribute type in a library.</summary>
    private static HashSet<string> ClassPaths(XElement lib, string name)
    {
        var paths = new HashSet<string>(StringComparer.Ordinal);
        void Walk(XElement parent, string prefix)
        {
            foreach (var child in parent.Elements().Where(e => ClassElements.Contains(e.Name.LocalName)))
            {
                var path = prefix + "/" + (string?)child.Attribute("Name");
                paths.Add(path);
                Walk(child, path);
            }
        }
        Walk(lib, name);
        return paths;
    }
}
