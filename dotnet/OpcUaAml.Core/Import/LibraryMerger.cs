// Brings the libraries of a converted NodeSet into an existing document.
//
// Libraries are the unit of exchange: Opc2Aml generates one ATL, ICL, RCL and
// SUC library per UA namespace, and every class in them is addressed by a path
// that starts with the library name. Replacing a library as a whole therefore
// keeps every reference into it valid, as long as the class names survive,
// while merging class by class could leave a half-updated namespace behind.
//
// The AutomationML base libraries come along with every conversion, in the
// version the converter carries. A document that already gets them through an
// ExternalReference keeps that one: the imported classes are pointed at it
// (Alias@Library/Class) instead of adding a second, older copy, which the
// editor reports as libraries that cannot be verified. That happens only when
// the referenced file holds every class the import points at.

using System.Xml.Linq;
using Aml.Engine.CAEX;

namespace OpcUaAml.Import;

public enum LibraryAction { Added, Replaced, Kept }

public sealed record LibraryChange(string Kind, string Name, LibraryAction Action, string? ModelVersion, string? Note = null);

public sealed record MergeReport(IReadOnlyList<LibraryChange> Changes)
{
    public int Added => Changes.Count(c => c.Action == LibraryAction.Added);
    public int Replaced => Changes.Count(c => c.Action == LibraryAction.Replaced);
    public int Kept => Changes.Count(c => c.Action == LibraryAction.Kept);

    public override string ToString() =>
        $"{Added} librar{(Added == 1 ? "y" : "ies")} added, {Replaced} replaced, {Kept} kept";
}

public sealed class MergeOptions
{
    /// <summary>
    /// Replace a library the document already has when it was generated from
    /// OPC UA (see <see cref="LibraryMerger.IsGeneratedLibrary"/>). Off, an
    /// existing library is kept and the imported one dropped.
    /// </summary>
    public bool ReplaceGeneratedLibraries { get; init; } = true;

    /// <summary>
    /// Replace a library even when the document's copy was generated from a
    /// newer publication of the same model. Off, the newer copy stays and the
    /// change carries a note saying so.
    /// </summary>
    public bool AllowDowngrade { get; init; }

    /// <summary>
    /// The folder of the target document, where the files its ExternalReferences
    /// name are looked for. Without it, external references are not looked into.
    /// </summary>
    public string? DocumentFolder { get; init; }
}

public static class LibraryMerger
{
    /// <summary>The CAEX version the Annex A libraries are written in.</summary>
    public const string RequiredSchemaVersion = "3.0";

    /// <summary>
    /// True for libraries Annex A generates: one per UA namespace (prefix and
    /// namespace URI) and the OpcAmlMetaModel libraries. The AutomationML base
    /// libraries are not generated and are never replaced.
    /// </summary>
    public static bool IsGeneratedLibrary(string name) =>
        name.StartsWith("ATL_", StringComparison.Ordinal)
        || name.StartsWith("ICL_", StringComparison.Ordinal)
        || name.StartsWith("RCL_", StringComparison.Ordinal)
        || name.StartsWith("SUC_", StringComparison.Ordinal);

    /// <summary>
    /// Copies every library of <paramref name="source"/> into
    /// <paramref name="target"/>, keeping IDs: Opc2Aml encodes the UA NodeId in
    /// each ID, and a later import of the same namespace must hit the same IDs.
    /// </summary>
    /// <exception cref="ImportException">The target is not a CAEX 3.0 document.</exception>
    public static MergeReport Merge(CAEXDocument target, CAEXDocument source, MergeOptions? options = null)
    {
        options ??= new MergeOptions();
        var version = target.CAEXFile.SchemaVersion;
        if (version != RequiredSchemaVersion)
            throw new ImportException(
                $"The document uses CAEX {version}. The OPC UA libraries (OPC 10000-83 Annex A) need CAEX 3.0 " +
                "(AutomationML 2.10). Convert the document first, e.g. with the AutomationML Editor.");

        var changes = new List<LibraryChange>();
        var s = source.CAEXFile;
        var t = target.CAEXFile;
        var elsewhere = new Elsewhere(ExternalLibraries.Read(target, options.DocumentFolder), source);

        MergeKind("AttributeTypeLib", s.AttributeTypeLib, t.AttributeTypeLib, options, elsewhere, changes);
        MergeKind("InterfaceClassLib", s.InterfaceClassLib, t.InterfaceClassLib, options, elsewhere, changes);
        MergeKind("RoleClassLib", s.RoleClassLib, t.RoleClassLib, options, elsewhere, changes);
        MergeKind("SystemUnitClassLib", s.SystemUnitClassLib, t.SystemUnitClassLib, options, elsewhere, changes);

        return new MergeReport(changes);
    }

    private static void MergeKind<T>(string kind, CAEXSequence<T> from, CAEXSequence<T> into,
        MergeOptions options, Elsewhere elsewhere, List<LibraryChange> changes)
        where T : CAEXObject
    {
        foreach (var lib in from.ToList())
        {
            var existing = into.FirstOrDefault(x => x.Name == lib.Name);
            var incoming = OpcUaLibraryInfo.Read((CAEXBasicObject)(object)lib);
            string? note = null;

            if (existing == null && !IsGeneratedLibrary(lib.Name) && elsewhere.Provider(lib.Name) is { } provider)
            {
                if (elsewhere.Redirects.ContainsKey(lib.Name))
                {
                    changes.Add(new LibraryChange(kind, lib.Name, LibraryAction.Kept, null,
                        $"taken from the external reference {provider.Alias} ({provider.Path}, {provider.Version ?? "no version"})"));
                    continue;
                }
                note = $"the external reference {provider.Alias} ({provider.Path}) lacks " +
                       $"{string.Join(", ", elsewhere.Missing(lib.Name))}, so the import's copy is added";
            }

            if (existing != null)
            {
                var present = OpcUaLibraryInfo.Read((CAEXBasicObject)(object)existing);
                if (!IsGeneratedLibrary(lib.Name) || !options.ReplaceGeneratedLibraries)
                {
                    changes.Add(new LibraryChange(kind, lib.Name, LibraryAction.Kept, present?.ModelVersion));
                    continue;
                }
                if (!options.AllowDowngrade && IsNewer(present, incoming))
                {
                    changes.Add(new LibraryChange(kind, lib.Name, LibraryAction.Kept, present?.ModelVersion,
                        $"the document has {Describe(present!)}, the import only {Describe(incoming!)}"));
                    continue;
                }

                // Same place in the document, so a replaced library does not
                // jump to the end of the tree the user is looking at.
                var index = into.ToList().IndexOf(existing);
                existing.Remove();
                into.InsertAt(index, elsewhere.Rewrite(Detached(lib)));
                changes.Add(new LibraryChange(kind, lib.Name, LibraryAction.Replaced, incoming?.ModelVersion,
                    present != null && incoming != null && !Equals(present, incoming)
                        ? $"{Describe(present)} replaced by {Describe(incoming)}" : null));
                continue;
            }

            into.Insert(elsewhere.Rewrite(Detached(lib)), asFirst: false, asIs: true);
            changes.Add(new LibraryChange(kind, lib.Name, LibraryAction.Added, incoming?.ModelVersion, note));
        }
    }

    private static bool IsNewer(OpcUaLibraryInfo? present, OpcUaLibraryInfo? incoming) =>
        present?.PublicationDate != null && incoming?.PublicationDate != null
        && present.PublicationDate > incoming.PublicationDate;

    private static string Describe(OpcUaLibraryInfo info) =>
        $"{info.ModelVersion ?? "?"} ({info.PublicationDate:yyyy-MM-dd})";

    /// <summary>
    /// Which base libraries the target gets through an ExternalReference, and
    /// whether each holds every class the imported libraries point at.
    /// </summary>
    private sealed class Elsewhere
    {
        private readonly IReadOnlyDictionary<string, ExternalLibraries.Library> _external;
        private readonly Dictionary<string, List<string>> _missing = new(StringComparer.Ordinal);

        /// <summary>Library name to the alias its class paths get.</summary>
        public Dictionary<string, string> Redirects { get; } = new(StringComparer.Ordinal);

        public Elsewhere(IReadOnlyDictionary<string, ExternalLibraries.Library> external, CAEXDocument source)
        {
            _external = external;
            if (external.Count == 0) return;
            var needed = source.CAEXFile.Node.Descendants().Attributes()
                .Where(a => ClassPathAttributes.Contains(a.Name.LocalName))
                .Select(a => a.Value)
                .ToHashSet(StringComparer.Ordinal);
            foreach (var (name, library) in external)
            {
                var missing = needed.Where(p => p.StartsWith(name + "/", StringComparison.Ordinal) && !library.ClassPaths.Contains(p)).ToList();
                if (missing.Count == 0) Redirects[name] = library.Alias;
                else _missing[name] = missing.Select(p => p[(name.Length + 1)..]).Distinct().Order(StringComparer.Ordinal).ToList();
            }
        }

        public ExternalLibraries.Library? Provider(string name) => _external.GetValueOrDefault(name);

        public IEnumerable<string> Missing(string name) => _missing.GetValueOrDefault(name) ?? new List<string>();

        /// <summary>Points the class paths of a copied library at the external libraries.</summary>
        public T Rewrite<T>(T copy) where T : CAEXObject
        {
            if (Redirects.Count == 0) return copy;
            foreach (var attribute in ((CAEXBasicObject)(object)copy).Node.DescendantsAndSelf().Attributes()
                         .Where(a => ClassPathAttributes.Contains(a.Name.LocalName)).ToList())
            {
                var slash = attribute.Value.IndexOf('/');
                if (slash > 0 && Redirects.TryGetValue(attribute.Value[..slash], out var alias))
                    attribute.Value = alias + "@" + attribute.Value;
            }
            return copy;
        }
    }

    /// <summary>The CAEX attributes that hold a path to a class or an attribute type.</summary>
    internal static readonly HashSet<string> ClassPathAttributes = new(StringComparer.Ordinal)
    {
        "RefBaseClassPath", "RefBaseSystemUnitPath", "RefRoleClassPath", "RefBaseRoleClassPath", "RefAttributeType",
    };

    /// <summary>A deep copy with IDs unchanged, not attached to any document.</summary>
    private static T Detached<T>(T lib) where T : CAEXObject =>
        (T)lib.Copy(deepCopy: true, assignNewIDs: false, includeSubClasses: true);
}
