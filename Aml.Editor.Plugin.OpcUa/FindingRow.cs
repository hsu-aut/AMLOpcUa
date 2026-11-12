// A finding of the check as the list on the Check tab shows it: in the
// plugin's language, with the names the core put into its message.

using Aml.Editor.Plugin.OpcUa.Localization;
using OpcUaAml.Checks;

namespace Aml.Editor.Plugin.OpcUa;

public sealed class FindingRow(Finding finding)
{
    public Finding Finding { get; } = finding;

    public bool IsError => Finding.Severity == OpcUaAml.Checks.Severity.Error;

    public string Severity => IsError ? L.T("Error") : L.T("Warning");

    public string Rule => Finding.Rule;

    public string ElementPath => Finding.ElementPath;

    public string Message => Finding.Format is { } format ? L.T(format, Finding.Args.Cast<object?>().ToArray()) : Finding.Message;
}
