using Aml.Editor.Plugin.OpcUa;

namespace PluginTests;

/// <summary>
/// The plugin takes Aml.Engine from the editor. It was referenced as "the
/// newest 4.x", so a build took whatever nuget.org had that day, and the
/// plugin then needed an editor with at least that version.
/// </summary>
public class EditorDependencyTests
{
    [Fact]
    public void The_plugin_is_built_against_the_Aml_Engine_it_was_tested_with()
    {
        var engine = typeof(OpcUaPlugin).Assembly.GetReferencedAssemblies().Single(a => a.Name == "Aml.Engine");

        Assert.Equal(new Version(4, 5, 2, 0), engine.Version);
    }

    [Fact]
    public void The_core_is_built_against_the_same_Aml_Engine()
    {
        var engine = typeof(OpcUaAml.Import.OpcUaImport).Assembly.GetReferencedAssemblies().Single(a => a.Name == "Aml.Engine");

        Assert.Equal(new Version(4, 5, 2, 0), engine.Version);
    }
}
