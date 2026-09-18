// The result of running a model through two mappings and back: per
// criterion, how many items the original has and how many survive, with
// examples of what was lost.

using System.Text;

namespace OpcUaAml.Roundtrip;

public sealed record Criterion(string Name, int Total, int Kept, IReadOnlyList<string> LostExamples, string? Note = null)
{
    public double Share => Total == 0 ? 1 : (double)Kept / Total;
}

public sealed record RoundtripReport(
    string Subject,
    string Chain,
    IReadOnlyList<Criterion> Criteria,
    string? FailedStep,
    string? Error,
    TimeSpan Duration,
    IReadOnlyList<string> Notes)
{
    public bool Completed => FailedStep == null;

    public string ToMarkdown(int examples = 5)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"### {Subject}: {Chain}");
        sb.AppendLine();
        if (!Completed)
        {
            sb.AppendLine($"Stopped at **{FailedStep}**: {Error}");
            sb.AppendLine();
        }
        if (Criteria.Count > 0)
        {
            sb.AppendLine("| Criterion | Original | Kept | Share | Lost, for example |");
            sb.AppendLine("|---|---:|---:|---:|---|");
            foreach (var c in Criteria)
            {
                var lost = string.Join(", ", c.LostExamples.Take(examples).Select(e => $"`{e}`"));
                if (c.LostExamples.Count > examples) lost += $" (+{c.LostExamples.Count - examples})";
                var share = c.Total == 0 ? "n/a" : $"{c.Share:P0}";
                sb.AppendLine($"| {c.Name}{(c.Note != null ? $" ({c.Note})" : "")} | {c.Total} | {c.Kept} | {share} | {lost} |");
            }
            sb.AppendLine();
        }
        foreach (var n in Notes) sb.AppendLine($"- {n}");
        if (Notes.Count > 0) sb.AppendLine();
        sb.AppendLine($"Duration: {Duration.TotalSeconds:0.0} s");
        return sb.ToString();
    }
}
