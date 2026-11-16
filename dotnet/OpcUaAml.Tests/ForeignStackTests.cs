using System.Reflection;
using System.Runtime.Loader;
using Aml.Engine.CAEX;
using OpcUaAml.Server;

namespace OpcUaAml.Tests;

/// <summary>
/// In the AML Editor every plugin has a load context of its own, and another
/// plugin may bring an Opc.Ua.Core of its own (MTP Control brings 1.5.377).
/// The stack took the message types of the first Opc.Ua.Core in the process,
/// so AMLOpcUa could read no server's answer: BadDecodingError, "Could not
/// process OpenSecureChannelResponse" (reported 2026-11-16).
/// </summary>
[Trait("Speed", "Slow")]
public class ForeignStackTests
{
    /// <summary>The libraries of this test's folder in a load context of their own, as the editor loads a plugin.</summary>
    private sealed class PluginContext(string folder) : AssemblyLoadContext("plugin")
    {
        protected override Assembly? Load(AssemblyName name)
        {
            var path = Path.Combine(folder, name.Name + ".dll");
            return File.Exists(path) ? LoadFromAssemblyPath(path) : null;
        }
    }

    private static int FreePort()
    {
        var l = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        l.Start();
        var p = ((System.Net.IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return p;
    }

    private static string TempPki() => Path.Combine(Path.GetTempPath(), "amlopcua-test-pki-" + Guid.NewGuid().ToString("N")[..8]);

    /// <summary>
    /// This library as a plugin in the editor: in a context of its own, after
    /// this test's Opc.Ua.Core, which stands for the other plugin's.
    /// </summary>
    private static (AssemblyLoadContext Plugin, Assembly Core) LoadPlugin()
    {
        _ = typeof(Opc.Ua.ObjectIds).Assembly;
        var folder = Path.GetDirectoryName(typeof(UaClient).Assembly.Location)!;
        var plugin = new PluginContext(folder);
        return (plugin, plugin.LoadFromAssemblyPath(Path.Combine(folder, "OpcUaAml.Core.dll")));
    }

    /// <summary>An options object of the plugin's context, its properties set by name.</summary>
    private static object Options(Assembly core, Type type, params (string Name, object Value)[] values)
    {
        var pluginType = core.GetType(type.FullName!)!;
        var options = Activator.CreateInstance(pluginType)!;
        foreach (var (name, value) in values) pluginType.GetProperty(name)!.SetValue(options, value);
        return options;
    }

    /// <summary>Calls a static async method of the plugin's context and returns its result.</summary>
    private static async Task<object> CallAsync(Assembly core, Type type, string method, params object[] arguments)
    {
        var task = (Task)core.GetType(type.FullName!)!.GetMethod(method)!.Invoke(null, arguments)!;
        await task;
        return task.GetType().GetProperty("Result")!.GetValue(task)!;
    }

    [Fact]
    public async Task Connects_when_another_plugin_loaded_its_opc_ua_core_first()
    {
        var pki = TempPki();
        var doc = CAEXDocument.New_CAEXDocument();
        doc.CAEXFile.InstanceHierarchy.Append("Plant");
        await using var host = await AmlServerHost.StartAsync(doc, new AmlServerOptions { Port = FreePort(), PkiRoot = Path.Combine(pki, "server") });

        var (plugin, core) = LoadPlugin();
        var options = Options(core, typeof(UaConnectOptions),
            (nameof(UaConnectOptions.EndpointUrl), host.EndpointUrl),
            (nameof(UaConnectOptions.UseSecurity), false),
            (nameof(UaConnectOptions.AcceptUntrustedServerCertificates), true),
            (nameof(UaConnectOptions.PkiRoot), Path.Combine(pki, "client")));
        var client = await CallAsync(core, typeof(UaClient), nameof(UaClient.ConnectAsync), options, CancellationToken.None);
        await ((IAsyncDisposable)client).DisposeAsync();

        // The plugin's client worked with an Opc.Ua.Core of its own, not with the first one.
        Assert.NotSame(typeof(Opc.Ua.ObjectIds).Assembly, plugin.Assemblies.Single(a => a.GetName().Name == "Opc.Ua.Core"));
    }

    [Fact]
    public async Task Serves_when_another_plugin_loaded_its_opc_ua_core_first()
    {
        // The practice plant, run by the plugin; a client of the other context asks it.
        var pki = TempPki();
        var (_, core) = LoadPlugin();
        var options = Options(core, typeof(NodeSetServerOptions),
            (nameof(NodeSetServerOptions.Port), FreePort()),
            (nameof(NodeSetServerOptions.PkiRoot), Path.Combine(pki, "server")));
        var host = await CallAsync(core, typeof(NodeSetServerHost), nameof(NodeSetServerHost.StartAsync),
            new[] { DiTestServer.DiPath }, options, CancellationToken.None);
        await using var _ = (IAsyncDisposable)host;

        await using var client = await UaClient.ConnectAsync(new UaConnectOptions
        {
            EndpointUrl = (string)host.GetType().GetProperty(nameof(NodeSetServerHost.EndpointUrl))!.GetValue(host)!,
            UseSecurity = false, AcceptUntrustedServerCertificates = true, PkiRoot = Path.Combine(pki, "client"),
        });
        Assert.NotEmpty(await client.BrowseAsync());
    }

    [Fact]
    public async Task Serves_the_document_when_another_plugin_loaded_its_opc_ua_core_first()
    {
        var pki = TempPki();
        var (plugin, core) = LoadPlugin();
        // The document of the plugin's own Aml.Engine, as the editor hands it over.
        var engine = plugin.LoadFromAssemblyName(typeof(CAEXDocument).Assembly.GetName());
        var doc = engine.GetType(typeof(CAEXDocument).FullName!)!.GetMethod(nameof(CAEXDocument.New_CAEXDocument), Type.EmptyTypes)!.Invoke(null, null)!;
        var options = Options(core, typeof(AmlServerOptions),
            (nameof(AmlServerOptions.Port), FreePort()),
            (nameof(AmlServerOptions.PkiRoot), Path.Combine(pki, "server")));
        var host = await CallAsync(core, typeof(AmlServerHost), nameof(AmlServerHost.StartAsync), doc, options, CancellationToken.None);
        await using var _ = (IAsyncDisposable)host;

        await using var client = await UaClient.ConnectAsync(new UaConnectOptions
        {
            EndpointUrl = (string)host.GetType().GetProperty(nameof(AmlServerHost.EndpointUrl))!.GetValue(host)!,
            UseSecurity = false, AcceptUntrustedServerCertificates = true, PkiRoot = Path.Combine(pki, "client"),
        });
        Assert.NotEmpty(await client.BrowseAsync());
    }
}
