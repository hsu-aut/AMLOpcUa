using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Windows.Threading;
using Aml.Editor.Plugin.OpcUa;
using Aml.Editor.Plugin.OpcUa.Localization;

namespace PluginTests;

/// <summary>
/// A plant to practise on: the MPS 500 the plugin brings, served on this
/// computer with simulated values. Before, it took a terminal and the command
/// line tool, which a booth laptop or a first-time user does not have.
/// </summary>
public class PracticePlantTests
{
    /// <summary>Runs <paramref name="body"/> on a WPF thread whose awaits come back to it, with English texts.</summary>
    private static void OnUiThread(Func<Task> body)
    {
        Exception? failure = null;
        var german = L.German;
        L.German = false;
        try
        {
            var thread = new Thread(() =>
            {
                SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
                var frame = new DispatcherFrame();
                var task = body();
                task.ContinueWith(_ => frame.Continue = false, TaskScheduler.Default);
                Dispatcher.PushFrame(frame);
                try { task.GetAwaiter().GetResult(); }
                catch (Exception ex) { failure = ex; }
            });
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            thread.Join();
        }
        finally { L.German = german; }
        if (failure != null) throw new Xunit.Sdk.XunitException(failure.ToString());
    }

    /// <summary>A port nobody listens on, as the first of the ten the plant tries.</summary>
    private static int SomeFreePort()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    private static bool Listening(int port)
    {
        try
        {
            using var client = new TcpClient();
            client.Connect(IPAddress.Loopback, port);
            return true;
        }
        catch (SocketException) { return false; }
    }

    [Fact]
    public void The_plugin_brings_the_practice_plant()
    {
        Assert.True(File.Exists(OpcUaPlugin.PracticePlantFile), OpcUaPlugin.PracticePlantFile);
    }

    [Fact]
    public void Starting_the_practice_plant_serves_the_MPS_500_and_fills_in_its_address()
    {
        var pki = Directory.CreateTempSubdirectory("opcuaaml-plant-pki-").FullName;
        var port = SomeFreePort();
        string? endpoint = null, button = null, buttonAfter = null;
        IReadOnlyList<string>? served = null;
        bool listening = false, listeningAfter = true;
        try
        {
            OnUiThread(async () =>
            {
                var plugin = new OpcUaPlugin();
                await plugin.StartPracticePlantAsync(port, pki);
                endpoint = plugin.EndpointBox.Text;
                served = plugin.PracticePlant?.Served;
                button = plugin.PracticePlantText.Text;
                listening = Listening(port);

                await plugin.StopPracticePlantAsync();
                buttonAfter = plugin.PracticePlantText.Text;
                listeningAfter = Listening(port);
            });
        }
        finally
        {
            Directory.Delete(pki, recursive: true);
        }

        Assert.Equal($"opc.tcp://127.0.0.1:{port}/AMLOpcUa", endpoint);
        Assert.Contains("http://hsu-hh.de/UA/MPS500/", served!);
        Assert.True(listening);
        Assert.Equal("Stop practice plant", button);
        Assert.Equal("Practice plant", buttonAfter);
        Assert.False(listeningAfter);
    }

    [Fact]
    public void A_port_in_use_is_passed_over()
    {
        var taken = new TcpListener(IPAddress.Loopback, 0);
        taken.Start();
        try
        {
            var port = ((IPEndPoint)taken.LocalEndpoint).Port;

            Assert.Equal(port + 1, OpcUaPlugin.FreePort(port, 10));
        }
        finally { taken.Stop(); }
    }
}
