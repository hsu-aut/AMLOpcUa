// A plant to practise on: the MPS 500 learning factory the plugin brings
// (examples\MPS500.NodeSet2.xml), served on this computer with simulated
// values, so that connecting, taking types and nodes and keeping values live
// can be tried without a plant and without the command line.

using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Windows;
using System.Windows.Media;
using Aml.Editor.Plugin.OpcUa.Diagnostics;
using Aml.Editor.Plugin.OpcUa.Localization;
using OpcUaAml.Server;

namespace Aml.Editor.Plugin.OpcUa;

public partial class OpcUaPlugin
{
    private NodeSetServerHost? _plant;

    /// <summary>The practice plant while it runs.</summary>
    internal NodeSetServerHost? PracticePlant => _plant;

    /// <summary>The MPS 500 the plugin brings, beside its assembly.</summary>
    internal static string PracticePlantFile =>
        Path.Combine(Path.GetDirectoryName(typeof(OpcUaPlugin).Assembly.Location) ?? AppContext.BaseDirectory, "examples", "MPS500.NodeSet2.xml");

    /// <summary>The first of <paramref name="count"/> ports from <paramref name="first"/> that nothing on this computer listens on.</summary>
    internal static int? FreePort(int first, int count)
    {
        for (var port = first; port < first + count; port++)
        {
            try
            {
                var probe = new TcpListener(IPAddress.Loopback, port);
                probe.Start();
                probe.Stop();
                return port;
            }
            catch (SocketException) { /* taken, try the next */ }
        }
        return null;
    }

    private async void PracticePlantButton_Click(object sender, RoutedEventArgs e)
    {
        if (_plant != null) await StopPracticePlantAsync();
        else await StartPracticePlantAsync();
    }

    /// <summary>
    /// Starts the practice plant on the first free port from <paramref name="firstPort"/>
    /// and puts its address into the endpoint box. <paramref name="pkiRoot"/> replaces
    /// the server's certificate store (tests).
    /// </summary>
    internal async Task StartPracticePlantAsync(int firstPort = 4840, string? pkiRoot = null)
    {
        if (_plant != null) return;
        if (!File.Exists(PracticePlantFile))
        {
            SetStatus(L.T("The practice plant is missing from the plugin folder ({0}). Install the plugin again.", PracticePlantFile));
            return;
        }
        // An OPC UA discovery server or another server often holds 4840 already.
        if (FreePort(firstPort, 10) is not { } port)
        {
            SetStatus(L.T("The ports {0} to {1} are in use; the practice plant cannot start.", firstPort, firstPort + 9));
            return;
        }
        SetBusy(true, L.T("Starting the practice plant …"));
        try
        {
            var folders = KnownNodeSetFolders(_settings.NodeSetFolders, ModelsFolder, CloudCache, OpcfFolder, ServerNodeSetsFolder, ImportedFolder).ToList();
            var options = pkiRoot == null
                ? new NodeSetServerOptions { Port = port, Simulate = true, Folders = folders }
                : new NodeSetServerOptions { Port = port, Simulate = true, Folders = folders, PkiRoot = pkiRoot };
            var plant = await NodeSetServerHost.StartAsync(new[] { PracticePlantFile }, options);
            if (_released)
            {
                await plant.DisposeAsync();
                return;
            }
            _plant = plant;
            if (_client == null) EndpointBox.Text = plant.EndpointUrl;
            var message = L.T("The practice plant runs: the MPS 500 learning factory, {0} node(s) with simulated values, at {1}. Connect to it.", plant.Nodes, plant.EndpointUrl);
            PluginLog.Info(message);
            SetStatus(message);
        }
        catch (Exception ex)
        {
            PluginLog.Error("Starting the practice plant failed", ex);
            SetStatus(L.T("Starting the practice plant failed: {0}", ex.Message));
        }
        finally
        {
            SetBusy(false, null);
            UpdateServerState();
        }
    }

    /// <summary>Stops the practice plant; a connection to it is closed first, since it would only be reported lost.</summary>
    internal async Task StopPracticePlantAsync()
    {
        var plant = _plant;
        if (plant == null) return;
        if (_client != null && string.Equals(_client.EndpointUrl.TrimEnd('/'), plant.EndpointUrl.TrimEnd('/'), StringComparison.OrdinalIgnoreCase))
            await DisconnectAsync();
        _plant = null;
        UpdateServerState();
        try { await plant.DisposeAsync(); }
        catch (Exception ex) { PluginLog.Debug($"Stopping the practice plant: {ex.Message}"); }
        PluginLog.Info("Practice plant stopped.");
        SetStatus(L.T("Practice plant stopped."));
    }

    /// <summary>The button follows whether the plant runs.</summary>
    private void UpdatePracticePlantButton()
    {
        PracticePlantText.Text = _plant != null ? L.T("Stop practice plant") : L.T("Practice plant");
        PracticePlantGlyph.Text = _plant != null ? "" : "";
        PracticePlantGlyph.Foreground = _plant != null ? new SolidColorBrush(Color.FromRgb(0xC0, 0x30, 0x30)) : (Brush)FindResource("Create");
        PracticePlantButton.IsEnabled = !_busy;
    }
}
