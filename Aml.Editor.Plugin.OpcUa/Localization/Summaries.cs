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
        + (result.InstancesNotTaken > 0 ? L.T(", {0} instance(s) of the model not taken (only its types are imported)", result.InstancesNotTaken) : "")
        + (result.Warnings.Count > 0 ? L.T(", {0} warning(s)", result.Warnings.Count) : "")
        + (result.Conversion.FromCache
            ? L.T(" ({0:0.0} s, converted before).", result.Conversion.Duration.TotalSeconds)
            : L.T(" ({0:0.0} s).", result.Conversion.Duration.TotalSeconds));
}
