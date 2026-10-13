using System.IO;
using Aml.Editor.Plugin.OpcUa;

namespace PluginTests;

/// <summary>
/// The plugin keeps NodeSets of its own: models applied from the modeler, and
/// what it fetched from the Cloud Library, the OPC Foundation and servers.
/// Import and modeler look for a model in all of them.
/// </summary>
public class OwnFoldersTests
{
    [Fact]
    public void The_own_folders_are_the_models_and_everything_fetched()
    {
        var root = Directory.CreateTempSubdirectory("opcuaaml-own-").FullName;
        try
        {
            string Folder(params string[] parts) => Directory.CreateDirectory(Path.Combine(new[] { root }.Concat(parts).ToArray())).FullName;
            var models = Folder("models");
            var cloud = Folder("cloudlibrary");
            var opcf = Folder("opcfoundation", "files");
            var machinery = Folder("opcfoundation", "files", "Machinery");
            var server = Folder("server-nodesets", "urn_plant");

            var own = OpcUaPlugin.OwnNodeSetFolders(models, cloud, Path.Combine(root, "opcfoundation"), Path.Combine(root, "server-nodesets")).ToList();

            Assert.Equal(models, own[0]);
            Assert.Contains(cloud, own);
            Assert.Contains(opcf, own);
            Assert.Contains(machinery, own);
            Assert.Contains(server, own);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Folders_not_made_yet_are_left_out()
    {
        var root = Path.Combine(Path.GetTempPath(), "opcuaaml-own-missing-" + Guid.NewGuid().ToString("N"));

        Assert.Empty(OpcUaPlugin.OwnNodeSetFolders(Path.Combine(root, "a"), Path.Combine(root, "b"), Path.Combine(root, "c"), Path.Combine(root, "d")));
    }
}
