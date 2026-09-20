using System.Text.Json;
using OpcUaAml.Types;
using Xunit.Abstractions;

namespace OpcUaAml.Tests;

/// <summary>
/// Cases shared with NodeSet.js, which implements instantiation a second
/// time in TypeScript: for DI types and chosen Optional children, which
/// children a new instance gets. The file lives in NodeSet.js
/// (tests/shared/instantiation.json) and both test suites read it, so the two
/// implementations cannot drift apart unnoticed.
/// </summary>
public class SharedCasesTests(DiDocument di, ITestOutputHelper output) : IClassFixture<DiDocument>
{
    private sealed record Case(string Type, string[] Optional, string[] Children);
    private sealed record Cases(string Model, Case[] cases);

    /// <summary>The shared file next to this repository, under the modeler's name or its former one.</summary>
    private static string? SharedFile()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            if (!File.Exists(Path.Combine(dir.FullName, "AMLOpcUa.sln"))) continue;
            return new[] { "NodeSet.js", "InfoModel.js", "UaModeler.js" }
                .Select(name => Path.Combine(dir.Parent!.FullName, name, "tests", "shared", "instantiation.json"))
                .FirstOrDefault(File.Exists);
        }
        return null;
    }

    [Fact]
    public void Instances_get_the_same_children_as_in_NodeSet_js()
    {
        var file = SharedFile();
        if (file == null)
        {
            output.WriteLine("NodeSet.js is not next to this repository; the shared cases were not checked.");
            return;
        }
        var cases = JsonSerializer.Deserialize<Cases>(File.ReadAllText(file), new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
        Assert.Equal(Fixtures.DiUri, cases.Model);
        Assert.NotEmpty(cases.cases);

        var differences = new List<string>();
        foreach (var c in cases.cases)
        {
            var result = TypeInstantiator.Instantiate(di.Type(c.Type), "X", new InstantiationOptions
            {
                IncludeOptional = p => c.Optional.Contains(p),
                AllowAbstract = true,
            });
            var here = result.Included.OrderBy(p => p, StringComparer.Ordinal).ToList();
            var there = c.Children.OrderBy(p => p, StringComparer.Ordinal).ToList();
            var onlyHere = here.Except(there).ToList();
            var onlyThere = there.Except(here).ToList();
            if (onlyHere.Count + onlyThere.Count > 0)
                differences.Add($"{c.Type} [{string.Join(",", c.Optional)}]: only AMLOpcUa {string.Join(", ", onlyHere)}; only NodeSet.js {string.Join(", ", onlyThere)}");
        }
        Assert.True(differences.Count == 0, string.Join("\n", differences));
    }
}
