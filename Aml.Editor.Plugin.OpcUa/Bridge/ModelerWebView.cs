// Hosts the graphical modeler (UaModeler.js) in a WebView2 control.
//
// The modeler's web build ships in the plugin folder (uamodeler-assets) and is
// served under a virtual host. Messages are JSON: the plugin sends a NodeSet
// to edit or asks for a new model, the page answers "ready" once it listens
// and "apply" with the NodeSet when the user takes it into the document. A
// message sent before "ready" would be lost, so it waits until then; a reload
// or a crashed renderer puts the bridge back into waiting.

using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace Aml.Editor.Plugin.OpcUa.Bridge;

public sealed class ModelerWebView : IDisposable
{
    private const string VirtualHost = "uamodeler.local";
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    private readonly WebView2 _view;
    private bool _started;
    private bool _ready;
    private bool _disposed;
    private string? _pending;

    /// <summary>The user applied the model: the NodeSet XML and its model URI.</summary>
    public event Action<string, string>? Applied;
    public event Action<bool>? DirtyChanged;
    public event Action<string, bool>? Status;
    public event Action<string>? Error;

    public ModelerWebView(WebView2 view) => _view = view;

    public bool IsDirty { get; private set; }

    public async Task InitAsync()
    {
        if (_started || _disposed) return;
        _started = true;
        // WebView2 keeps its profile next to the executable unless told
        // otherwise; under Program Files that folder is not writable.
        _view.CreationProperties = new CoreWebView2CreationProperties
        {
            UserDataFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AMLOpcUa", "WebView2"),
        };
        try { await _view.EnsureCoreWebView2Async(); }
        catch (Exception ex)
        {
            Error?.Invoke("The WebView2 runtime is missing or failed to start: " + ex.Message);
            return;
        }
        if (_disposed) return;

        var assets = AssetsPath();
        if (!File.Exists(Path.Combine(assets, "index.html")))
        {
            Error?.Invoke($"The modeler is missing from the plugin folder ({assets}).");
            return;
        }
        var core = _view.CoreWebView2;
        core.Settings.AreDevToolsEnabled = true;
        core.Settings.IsStatusBarEnabled = false;
        core.SetVirtualHostNameToFolderMapping(VirtualHost, assets, CoreWebView2HostResourceAccessKind.Allow);
        core.WebMessageReceived += OnMessage;
        core.NavigationStarting += (_, _) => _ready = false;
        core.ProcessFailed += (_, e) =>
        {
            _ready = false;
            Error?.Invoke($"The modeler stopped ({e.ProcessFailedKind}); reload it with the button above.");
        };
        core.Navigate($"https://{VirtualHost}/index.html");
    }

    public void Reload()
    {
        if (_view.CoreWebView2 != null) _view.CoreWebView2.Reload();
    }

    /// <summary>Opens a NodeSet in the modeler, with the NodeSets it requires that the modeler does not bundle.</summary>
    public void Open(string name, string xml, IEnumerable<string> required) =>
        Send(new { type = "open", name, xml, required = required.ToArray() });

    public void NewModel(string modelUri, IEnumerable<string> required) =>
        Send(new { type = "new", modelUri, required = required.ToArray() });

    private void Send(object message)
    {
        var json = JsonSerializer.Serialize(message, Json);
        if (!_ready || _view.CoreWebView2 == null) { _pending = json; return; }
        _view.CoreWebView2.PostWebMessageAsJson(json);
    }

    private void OnMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        if (_disposed) return;
        Message? m;
        try { m = JsonSerializer.Deserialize<Message>(e.WebMessageAsJson, Json); }
        catch (JsonException ex) { Error?.Invoke("A message from the modeler could not be read: " + ex.Message); return; }
        switch (m?.Type)
        {
            case "ready":
                _ready = true;
                if (_pending is { } p) { _pending = null; _view.CoreWebView2.PostWebMessageAsJson(p); }
                break;
            case "apply" when m.Xml != null:
                Applied?.Invoke(m.Xml, m.ModelUri ?? "");
                break;
            case "dirty":
                IsDirty = m.Dirty == true;
                DirtyChanged?.Invoke(IsDirty);
                break;
            case "status":
                Status?.Invoke(m.Text ?? "", m.Warn == true);
                break;
        }
    }

    private static string AssetsPath()
    {
        var location = typeof(ModelerWebView).Assembly.Location;
        var dir = string.IsNullOrEmpty(location) ? AppContext.BaseDirectory : Path.GetDirectoryName(location)!;
        return Path.Combine(dir, "uamodeler-assets");
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { if (_view.CoreWebView2 != null) _view.CoreWebView2.WebMessageReceived -= OnMessage; }
        catch (InvalidOperationException) { /* the control is gone already */ }
    }

    private sealed record Message(string? Type, string? Xml, string? ModelUri, bool? Dirty, string? Text, bool? Warn);
}
