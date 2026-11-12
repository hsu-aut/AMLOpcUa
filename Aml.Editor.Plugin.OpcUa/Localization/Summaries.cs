// Summaries the plugin writes from results of the core, in its language. The
// core's own summaries are English, for the command line.

using OpcUaAml.Import;

namespace Aml.Editor.Plugin.OpcUa.Localization;

internal static class Summaries
{
    /// <summary>"Imported …: 12 library(ies) added, 0 replaced, 3 kept (19.1 s)."</summary>
    public static string Import(ImportResult result) =>
        L.T("Imported {0}: {1} library(ies) added, {2} replaced, {3} kept",
            result.Conversion.NodeSet.PrimaryModel.Model.ModelUri, result.Merge.Added, result.Merge.Replaced, result.Merge.Kept)
        + (result.InstancesNotTaken > 0 ? L.T(", {0} instance(s) of the model not taken (an import brings types; the modeler's Apply to document brings the instances too)", result.InstancesNotTaken) : "")
        + (result.Warnings.Count > 0 ? L.T(", {0} warning(s)", result.Warnings.Count) : "")
        + (result.Conversion.FromCache
            ? L.T(" ({0:0.0} s, converted before).", result.Conversion.Duration.TotalSeconds)
            : L.T(" ({0:0.0} s).", result.Conversion.Duration.TotalSeconds));

    /// <summary>Reading and keeping values live found nothing to work on; says how to change that.</summary>
    public static string NothingBound =>
        L.T("No element of this document is bound to this server. Take its nodes into the document (Take into document) or bind an element of your own to a variable (Bind to element…).");

    /// <summary>"Read 3 bound value(s) of opc.tcp://… into the document. Press Ctrl+S to save."</summary>
    public static string Snapshot(OpcUaAml.Server.SnapshotResult result, string endpoint)
    {
        // Problems are the failed reads and the bindings the server could not resolve.
        var unresolved = result.Problems.Count - result.Failed;
        if (result.Read == 0 && result.Failed == 0 && result.Skipped == 0 && unresolved <= 0) return NothingBound;
        return L.T("Read {0} bound value(s) of {1} into the document", result.Read, endpoint)
            + (result.Skipped > 0 ? L.T("; {0} bound to another server left out", result.Skipped) : "")
            + (result.Failed > 0 ? L.T("; {0} could not be read (see log)", result.Failed) : "")
            + (unresolved > 0 ? L.T("; {0} not on this server (see log)", unresolved) : "")
            + "."
            + (result.Read > 0 ? L.T(" Press Ctrl+S to save.") : "");
    }

    /// <summary>"1 instance(s) with 274 element(s) taken into IH_MPS500."</summary>
    public static string Transfer(InstanceTransferResult result) =>
        L.T("{0} instance(s) with {1} element(s) taken into {2}", result.Taken, result.Elements, result.Where)
        + (result.LinksLeftOut > 0 ? L.T(", {0} link(s) to nodes outside left out", result.LinksLeftOut) : "")
        + ".";
}
