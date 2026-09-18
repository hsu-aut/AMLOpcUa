// Brings the libraries of a converted NodeSet into an existing document.
//
// Libraries are the unit of exchange: Opc2Aml generates one ATL, ICL, RCL and
// SUC library per UA namespace, and every class in them is addressed by a path
// that starts with the library name. Replacing a library as a whole therefore
// keeps every reference into it valid, as long as the class names survive,
// while merging class by class could leave a half-updated namespace behind.

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

        MergeKind("AttributeTypeLib", s.AttributeTypeLib, t.AttributeTypeLib, options, changes);
        MergeKind("InterfaceClassLib", s.InterfaceClassLib, t.InterfaceClassLib, options, changes);
        MergeKind("RoleClassLib", s.RoleClassLib, t.RoleClassLib, options, changes);
        MergeKind("SystemUnitClassLib", s.SystemUnitClassLib, t.SystemUnitClassLib, options, changes);

        return new MergeReport(changes);
    }

    private static void MergeKind<T>(string kind, CAEXSequence<T> from, CAEXSequence<T> into,
        MergeOptions options, List<LibraryChange> changes)
        where T : CAEXObject
    {
        foreach (var lib in from.ToList())
        {
            var existing = into.FirstOrDefault(x => x.Name == lib.Name);
            var incoming = OpcUaLibraryInfo.Read((CAEXBasicObject)(object)lib);

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
                into.InsertAt(index, Detached(lib));
                changes.Add(new LibraryChange(kind, lib.Name, LibraryAction.Replaced, incoming?.ModelVersion,
                    present != null && incoming != null && !Equals(present, incoming)
                        ? $"{Describe(present)} replaced by {Describe(incoming)}" : null));
                continue;
            }

            into.Insert(Detached(lib), asFirst: false, asIs: true);
            changes.Add(new LibraryChange(kind, lib.Name, LibraryAction.Added, incoming?.ModelVersion));
        }
    }

    private static bool IsNewer(OpcUaLibraryInfo? present, OpcUaLibraryInfo? incoming) =>
        present?.PublicationDate != null && incoming?.PublicationDate != null
        && present.PublicationDate > incoming.PublicationDate;

    private static string Describe(OpcUaLibraryInfo info) =>
        $"{info.ModelVersion ?? "?"} ({info.PublicationDate:yyyy-MM-dd})";

    /// <summary>A deep copy with IDs unchanged, not attached to any document.</summary>
    private static T Detached<T>(T lib) where T : CAEXObject =>
        (T)lib.Copy(deepCopy: true, assignNewIDs: false, includeSubClasses: true);
}
