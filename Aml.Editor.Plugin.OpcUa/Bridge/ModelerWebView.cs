// Hosts the graphical modeler (NodeSet.js) in a WebView2 control.
//
// The modeler's web build ships in the plugin folder (modeler-assets) and is
// served under a virtual host. Messages are JSON: the plugin sends a NodeSet
// to edit or asks for a new model, the page answers "ready" once it listens,
// "apply" with the NodeSet when the user takes it into the document and "save"
// when the user saves it; the plugin answers both ("applied", "saved"). A
// message sent before "ready" would be lost, so it waits until then; a reload
// or a crashed renderer puts the bridge back into waiting.
//
// The control shows the modeler and nothing else: navigation away from the
// virtual host and new windows are refused, and messages count only from it,
// since whatever sends "apply" writes into the document.

using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace Aml.Editor.Plugin.OpcUa.Bridge;

public sealed class ModelerWebView : IDisposable
{
    private const string VirtualHost = "nodeset.local";
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    private readonly WebView2 _view;
    private bool _started;
    private bool _ready;
    private bool _disposed;
    private string? _pending;

    /// <summary>The user applied the model: the NodeSet XML and its model URI. Answer with <see cref="Reply"/>.</summary>
    public event Action<string, string>? Applied;

    /// <summary>The user saves the model: the NodeSet XML and a file name. Answer with <see cref="Reply"/>.</summary>
    public event Action<string, string>? SaveRequested;
    public event Action<bool>? DirtyChanged;
    public event Action<string, bool>? Status;
    public event Action<string>? Error;

    public ModelerWebView(WebView2 view) => _view = view;

    public bool IsDirty { get; private set; }

    /// <summary>Whether the editor shows its dark theme; the page follows it.</summary>
    public bool Dark { get; private set; }

    /// <summary>Tells the page the editor's theme, now or once it is ready.</summary>
    public void SetTheme(bool dark)
    {
        Dark = dark;
        if (_view.CoreWebView2 != null)
            _view.DefaultBackgroundColor = dark ? System.Drawing.Color.FromArgb(255, 0x1F, 0x1F, 0x1F) : System.Drawing.Color.White;
        if (_ready) _view.CoreWebView2?.PostWebMessageAsJson(JsonSerializer.Serialize(new { type = "theme", dark }, Json));
    }

    public async Task InitAsync()
    {
        if (_started || _disposed) return;
        _started = true;
        try
        {
            await StartAsync();
        }
        catch
        {
            // A later visit to the tab tries again.
            _started = false;
            throw;
        }
    }

    private async Task StartAsync()
    {
        // WebView2 keeps its profile next to the executable unless told
        // otherwise; under Program Files that folder is not writable.
        _view.CreationProperties = new CoreWebView2CreationProperties
        {
            UserDataFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AMLOpcUa", "WebView2"),
        };
        try { await _view.EnsureCoreWebView2Async(); }
        catch (Exception ex)
        {
            _started = false;
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
        // No white flash before the page has its theme.
        _view.DefaultBackgroundColor = Dark ? System.Drawing.Color.FromArgb(255, 0x1F, 0x1F, 0x1F) : System.Drawing.Color.White;
#if DEBUG
        core.Settings.AreDevToolsEnabled = true;
#else
        core.Settings.AreDevToolsEnabled = false;
#endif
        core.Settings.IsStatusBarEnabled = false;
        core.SetVirtualHostNameToFolderMapping(VirtualHost, assets, CoreWebView2HostResourceAccessKind.Allow);
        core.WebMessageReceived += OnMessage;
        core.NavigationStarting += (_, e) =>
        {
            if (!IsModeler(e.Uri)) { e.Cancel = true; return; }
            _ready = false;
        };
        core.NewWindowRequested += (_, e) => e.Handled = true;
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

    /// <summary>Answers "apply" ("applied") or "save" ("saved"): whether it worked, and what to show.</summary>
    public void Reply(string type, bool ok, string text) => Send(new { type, ok, text });

    private static bool IsModeler(string? uri) =>
        Uri.TryCreate(uri, UriKind.Absolute, out var u) && u.Scheme == Uri.UriSchemeHttps && u.Host == VirtualHost;

    private void Send(object message)
    {
        var json = JsonSerializer.Serialize(message, Json);
        if (!_ready || _view.CoreWebView2 == null) { _pending = json; return; }
        _view.CoreWebView2.PostWebMessageAsJson(json);
    }

    private void OnMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        if (_disposed || !IsModeler(e.Source)) return;
        Message? m;
        try { m = JsonSerializer.Deserialize<Message>(e.WebMessageAsJson, Json); }
        catch (JsonException ex) { Error?.Invoke("A message from the modeler could not be read: " + ex.Message); return; }
        switch (m?.Type)
        {
            case "ready":
                _ready = true;
                _view.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(new { type = "theme", dark = Dark }, Json));
                if (_pending is { } p) { _pending = null; _view.CoreWebView2.PostWebMessageAsJson(p); }
                break;
            case "apply" when m.Xml != null:
                Applied?.Invoke(m.Xml, m.ModelUri ?? "");
                break;
            case "save" when m.Xml != null:
                SaveRequested?.Invoke(m.Xml, m.Name ?? "Model.NodeSet2.xml");
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
        return Path.Combine(dir, "modeler-assets");
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { if (_view.CoreWebView2 != null) _view.CoreWebView2.WebMessageReceived -= OnMessage; }
        catch (InvalidOperationException) { /* the control is gone already */ }
        try { _view.Dispose(); }
        catch (InvalidOperationException) { /* the control is gone already */ }
    }

    private sealed record Message(string? Type, string? Xml, string? ModelUri, string? Name, bool? Dirty, string? Text, bool? Warn);
}
