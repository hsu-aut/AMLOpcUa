// Converts a NodeSet in a process of its own: the converter the plugin brings
// in its "converter" folder (uaaml convert), which writes the result to a file
// the plugin reads back.
//
// Inside the AutomationML Editor, Aml.Engine is the editor's: a version of its
// own, with services the editor registers for the whole process. Its undo
// service recorded every change of the converter's documents and its unique
// name service rewrote references on every inserted element, so a conversion
// of seconds ran for many minutes and could rename what Annex A names on
// purpose. In a process of its own the conversion is the one the command line
// and the tests run, with the Aml.Engine it was built against; a converter
// that crashes no longer takes the editor with it, and its memory is given back.

using System.Diagnostics;
using System.IO;
using OpcUaAml.Import;

namespace Aml.Editor.Plugin.OpcUa;

internal static class ConverterProcess
{
    /// <summary>The converter the plugin brings: converter\uaaml.exe beside the plugin's assembly.</summary>
    public static string DefaultLocation =>
        Path.Combine(Path.GetDirectoryName(typeof(ConverterProcess).Assembly.Location) ?? AppContext.BaseDirectory, "converter", "uaaml.exe");

    /// <summary>Converts <paramref name="nodeSet"/>, looking for the models it requires in <paramref name="folders"/>.</summary>
    /// <param name="converter">The converter to run; the plugin's own when null.</param>
    /// <exception cref="ImportException">The converter is missing, or it reported an error.</exception>
    public static async Task<(ConversionResult Result, IReadOnlyList<string> CatalogNotes)> ConvertAsync(
        string nodeSet, IEnumerable<string> folders, string? converter = null, CancellationToken token = default)
    {
        converter ??= DefaultLocation;
        if (!File.Exists(converter))
            throw new ImportException($"The converter is missing from the plugin folder ({converter}). Install the plugin again.");

        var work = Directory.CreateTempSubdirectory("opcuaaml-convert-");
        try
        {
            var output = Path.Combine(work.FullName, "converted.aml");
            var start = new ProcessStartInfo(converter)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            start.ArgumentList.Add("convert");
            start.ArgumentList.Add(nodeSet);
            foreach (var folder in folders)
            {
                start.ArgumentList.Add("--search");
                start.ArgumentList.Add(folder);
            }
            start.ArgumentList.Add("-o");
            start.ArgumentList.Add(output);

            using var process = Process.Start(start) ?? throw new ImportException($"The converter did not start ({converter}).");
            var standardOutput = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
            var standardError = process.StandardError.ReadToEndAsync(CancellationToken.None);
            try
            {
                await process.WaitForExitAsync(token);
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(entireProcessTree: true); }
                catch (InvalidOperationException) { /* already gone */ }
                throw;
            }
            await standardOutput;
            var errors = await standardError;
            if (process.ExitCode != 0) throw new ImportException(Failure(errors, process.ExitCode));

            return await Task.Run(() => ConversionTransfer.Read(output, nodeSet), token);
        }
        finally
        {
            try { work.Delete(recursive: true); }
            catch (IOException) { /* a scanner holding the file; the OS cleans temp */ }
            catch (UnauthorizedAccessException) { }
        }
    }

    /// <summary>What the converter said went wrong ("error: …" on its error output), or how it ended.</summary>
    private static string Failure(string errors, int exitCode)
    {
        var reported = errors.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Where(l => l.StartsWith("error: ", StringComparison.Ordinal))
            .Select(l => l["error: ".Length..])
            .ToList();
        return reported.Count > 0
            ? string.Join(" ", reported)
            : $"The converter ended unexpectedly (exit code {exitCode}). {errors.Trim()}".Trim();
    }
}
