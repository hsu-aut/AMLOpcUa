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

    /// <summary>
    /// "Types of the server…" fetches only the models it does not have. It
    /// looked in the models folder and the NodeSet folders alone, so a model
    /// fetched from the OPC Foundation was fetched again from the server, often
    /// rebuilt by browsing, with less in it.
    /// </summary>
    [Fact]
    public void Types_of_a_server_find_a_model_fetched_from_the_OPC_Foundation()
    {
        var root = Directory.CreateTempSubdirectory("opcuaaml-own-").FullName;
        try
        {
            string Folder(params string[] parts) => Directory.CreateDirectory(Path.Combine(new[] { root }.Concat(parts).ToArray())).FullName;
            var fetched = Folder("opcfoundation", "files", "Mini");
            File.Copy(Mini(), Path.Combine(fetched, "Mini.NodeSet2.xml"));

            var catalog = OpcUaAml.NodeSets.NodeSetCatalog.Create(OpcUaPlugin.KnownNodeSetFolders(Array.Empty<string>(),
                Path.Combine(root, "models"), Path.Combine(root, "cloudlibrary"), Path.Combine(root, "opcfoundation"), Path.Combine(root, "server-nodesets")));

            Assert.NotNull(catalog.Find("http://example.org/Mini/"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>
    /// "Edit in the modeler" looks for a namespace's NodeSet in the known
    /// folders. A file imported from anywhere else (Downloads, a USB stick) was
    /// not found: "No NodeSet of … found".
    /// </summary>
    [Fact]
    public void A_NodeSet_imported_from_another_folder_is_found_again()
    {
        var root = Directory.CreateTempSubdirectory("opcuaaml-own-").FullName;
        try
        {
            var downloads = Directory.CreateDirectory(Path.Combine(root, "Downloads")).FullName;
            var file = Path.Combine(downloads, "Mini.NodeSet2.xml");
            File.Copy(Mini(), file);
            var imported = Path.Combine(root, "imported");

            OpcUaPlugin.KeepImportedCopy(file, imported, Array.Empty<string>());
            Directory.Delete(downloads, recursive: true);
            var own = OpcUaPlugin.OwnNodeSetFolders(Path.Combine(root, "models"), Path.Combine(root, "cloud"),
                Path.Combine(root, "opcf"), Path.Combine(root, "servers"), imported);

            Assert.NotNull(OpcUaAml.NodeSets.NodeSetCatalog.Create(own).Find("http://example.org/Mini/"));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void A_NodeSet_from_a_known_folder_is_not_copied()
    {
        var root = Directory.CreateTempSubdirectory("opcuaaml-own-").FullName;
        try
        {
            var known = Directory.CreateDirectory(Path.Combine(root, "nodesets")).FullName;
            var file = Path.Combine(known, "Mini.NodeSet2.xml");
            File.Copy(Mini(), file);
            var imported = Path.Combine(root, "imported");

            OpcUaPlugin.KeepImportedCopy(file, imported, new[] { known });

            Assert.False(Directory.Exists(imported) && Directory.EnumerateFiles(imported).Any());
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static string Mini()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "AMLOpcUa.sln")))
                return Path.Combine(dir.FullName, "dotnet", "OpcUaAml.Tests", "Fixtures", "modeldesign", "Mini.NodeSet2.xml");
        throw new DirectoryNotFoundException("AMLOpcUa.sln was not found above the test folder.");
    }

    [Fact]
    public void Folders_not_made_yet_are_left_out()
    {
        var root = Path.Combine(Path.GetTempPath(), "opcuaaml-own-missing-" + Guid.NewGuid().ToString("N"));

        Assert.Empty(OpcUaPlugin.OwnNodeSetFolders(Path.Combine(root, "a"), Path.Combine(root, "b"), Path.Combine(root, "c"), Path.Combine(root, "d")));
    }
}
