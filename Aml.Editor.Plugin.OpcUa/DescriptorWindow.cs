// Save as UAFX Descriptor: what the manifest says (OPC 10000-83 7.3.2), the
// files to attach, and the certificate to sign with (8.2 asks every export to
// sign). The certificate comes from the plugin's own signing keys, from the
// user's Windows certificate store, or from a .pfx file; "New…" makes a
// self-signed one and keeps it.

using System.IO;
using System.Security.Cryptography.X509Certificates;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using Aml.Editor.Plugin.OpcUa.Localization;
using Microsoft.Win32;
using OpcUaAml.Descriptors;

namespace Aml.Editor.Plugin.OpcUa;

public sealed class DescriptorWindow : Window
{
    private readonly DescriptorKeys _keys;
    private readonly ListBox _attachments = new() { Height = 86 };
    private readonly TextBlock _info = DialogKit.Message();
    private readonly List<string> _files = new();

    internal TextBox Identifier { get; } = new() { Padding = new Thickness(3) };
    internal TextBox Version { get; } = new() { Padding = new Thickness(3), Width = 120, HorizontalAlignment = HorizontalAlignment.Left, Text = "1.0.0.0" };
    internal TextBox FxVersion { get; } = new() { Padding = new Thickness(3), Width = 120, HorizontalAlignment = HorizontalAlignment.Left };
    internal ComboBox Signers { get; } = new() { MinWidth = 380 };

    /// <summary>The manifest, once accepted.</summary>
    public DescriptorInfo? Info { get; private set; }

    /// <summary>The certificate to sign with, once accepted.</summary>
    public X509Certificate2? Signer { get; private set; }

    /// <summary>The files to attach.</summary>
    public IReadOnlyList<string> Attachments => _files;

    public DescriptorWindow(string documentName, byte[] document, DescriptorKeys keys, bool includeStore = true)
    {
        _keys = keys;
        Width = 680;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;

        Identifier.Text = "urn:example:" + Regex.Replace(documentName.ToLowerInvariant(), "[^a-z0-9._-]+", "-").Trim('-');
        var fx = DescriptorWriter.FxVersionOf(document);
        FxVersion.Text = fx ?? "";
        foreach (var own in keys.OwnSigners()) AddSigner(own, L.T("kept by the plugin"));
        if (includeStore)
            foreach (var stored in StoreSigners()) AddSigner(stored, L.T("Windows certificate store"));
        if (Signers.Items.Count > 0) Signers.SelectedIndex = 0;

        var form = new StackPanel();
        void Field(string label, UIElement input, string? hint = null)
        {
            form.Children.Add(DialogKit.Label(label));
            form.Children.Add(input);
            if (hint != null) form.Children.Add(new TextBlock { Text = hint, Foreground = DialogKit.Muted, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0) });
        }
        Field(L.T("Identifier (DescriptorIdentifier)"), Identifier, L.T("A URI that names this Descriptor and no other, such as urn:company:plant:component."));
        Field(L.T("Version (DescriptorVersion)"), Version, L.T("Up to four numbers separated by points: Major.Minor.Build.SubBuild."));
        Field(L.T("OPC UA FX version (OpcUaFxVersion)"), FxVersion, fx != null
            ? L.T("Read from the document's OPC UA FX AML library.")
            : L.T("The document uses no OPC UA FX AML library, so the Descriptor will not be an OPC UA FX information model as OPC 10000-83 asks; give the version it should adhere to."));

        var attachButtons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 0) };
        var add = DialogKit.Action(L.T("Add files…"));
        add.Click += (_, _) => AddFiles();
        var remove = DialogKit.Action(L.T("Remove"));
        remove.Margin = new Thickness(6, 0, 0, 0);
        remove.Click += (_, _) =>
        {
            if (_attachments.SelectedIndex < 0) return;
            _files.RemoveAt(_attachments.SelectedIndex);
            _attachments.Items.RemoveAt(_attachments.SelectedIndex);
        };
        attachButtons.Children.Add(add);
        attachButtons.Children.Add(remove);
        var attach = new StackPanel();
        attach.Children.Add(_attachments);
        attach.Children.Add(attachButtons);
        Field(L.T("Attachments"), attach, L.T("Manuals, pictures, anything that helps to use the component. An attached Descriptor (.amlx) is embedded as one."));

        var signRow = new DockPanel();
        var fromFile = DialogKit.Action(L.T("From file…"));
        fromFile.Margin = new Thickness(6, 0, 0, 0);
        fromFile.Click += (_, _) => SignerFromFile();
        var create = DialogKit.Action(L.T("New…"));
        create.Margin = new Thickness(6, 0, 0, 0);
        create.Click += (_, _) => NewSigner();
        DockPanel.SetDock(create, Dock.Right);
        DockPanel.SetDock(fromFile, Dock.Right);
        signRow.Children.Add(create);
        signRow.Children.Add(fromFile);
        signRow.Children.Add(Signers);
        Field(L.T("Sign with"), signRow, L.T("Every Descriptor is signed (OPC 10000-83 8.2). The receiver checks the signature and decides whether to trust the certificate."));

        var save = DialogKit.Action(L.T("Save…"), primary: true);
        save.Click += (_, _) => { if (Accept()) DialogResult = true; };
        DialogKit.Frame(this, "", DialogKit.Exchange, L.T("Save as UAFX Descriptor"),
            L.T("The document as an AML container for OPC UA FX offline engineering (OPC 10000-83): a manifest, the files you attach, your signature."),
            form, _info, save, DialogKit.Action(L.T("Cancel"), cancel: true));
    }

    /// <summary>Takes what is typed; false, with the reason shown, when the manifest cannot hold it or no signer is chosen.</summary>
    internal bool Accept()
    {
        var identifier = Identifier.Text.Trim();
        if (!Uri.TryCreate(identifier, UriKind.Absolute, out _))
        {
            DialogKit.ShowError(_info, L.T("The identifier is not a URI, such as urn:company:plant:component."));
            return false;
        }
        if (!DescriptorVersion.TryParse(Version.Text, out var version))
        {
            DialogKit.ShowError(_info, L.T("The version is up to four numbers from 0 to 65535 separated by points, such as 1.0.0.0."));
            return false;
        }
        var fx = FxVersion.Text.Trim();
        if (fx.Length == 0)
        {
            DialogKit.ShowError(_info, L.T("Give the version of OPC UA FX the Descriptor adheres to, such as 1.00.03."));
            return false;
        }
        if ((Signers.SelectedItem as ComboBoxItem)?.Tag is not X509Certificate2 signer)
        {
            DialogKit.ShowError(_info, L.T("Choose a certificate to sign with, or make one with New…."));
            return false;
        }
        Info = new DescriptorInfo(identifier, version, fx);
        Signer = signer;
        return true;
    }

    internal void AddAttachment(string path)
    {
        if (_files.Contains(path, StringComparer.OrdinalIgnoreCase)) return;
        _files.Add(path);
        _attachments.Items.Add(DialogKit.Entry(Path.GetFileName(path), Path.GetDirectoryName(path) ?? "", path, ""));
    }

    private void AddFiles()
    {
        var dialog = new OpenFileDialog { Title = L.T("Attach files"), Multiselect = true, Filter = L.T("All files (*.*)|*.*") };
        if (dialog.ShowDialog(this) != true) return;
        foreach (var file in dialog.FileNames) AddAttachment(file);
    }

    private void AddSigner(X509Certificate2 certificate, string source, bool select = false)
    {
        var name = certificate.GetNameInfo(X509NameType.SimpleName, false);
        var item = new ComboBoxItem
        {
            Content = L.T("{0}  ·  valid until {1:yyyy-MM-dd}  ·  {2}", name, certificate.NotAfter, source),
            Tag = certificate,
        };
        System.Windows.Automation.AutomationProperties.SetName(item, name);
        Signers.Items.Add(item);
        if (select) Signers.SelectedItem = item;
    }

    /// <summary>Certificates of the user's store that can sign: with a key, valid now, for digital signatures, no CA.</summary>
    private static IEnumerable<X509Certificate2> StoreSigners()
    {
        using var store = new X509Store(StoreName.My, StoreLocation.CurrentUser);
        try { store.Open(OpenFlags.ReadOnly | OpenFlags.OpenExistingOnly); }
        catch (System.Security.Cryptography.CryptographicException) { yield break; }
        foreach (var c in store.Certificates)
        {
            var usage = c.Extensions.OfType<X509KeyUsageExtension>().FirstOrDefault();
            var ca = c.Extensions.OfType<X509BasicConstraintsExtension>().FirstOrDefault()?.CertificateAuthority == true;
            if (c.HasPrivateKey && !ca && c.NotBefore <= DateTime.Now && DateTime.Now <= c.NotAfter
                && (usage == null || usage.KeyUsages.HasFlag(X509KeyUsageFlags.DigitalSignature))
                && (c.GetRSAPublicKey() != null || c.GetECDsaPublicKey() != null))
                yield return c;
        }
    }

    private void SignerFromFile()
    {
        var dialog = new OpenFileDialog { Title = L.T("Certificate to sign with"), Filter = L.T("Certificate with key (*.pfx;*.p12)|*.pfx;*.p12") };
        if (dialog.ShowDialog(this) != true) return;
        var password = new PasswordBox { Padding = new Thickness(3) };
        var ask = new Window { Owner = this, Width = 460, SizeToContent = SizeToContent.Height, ResizeMode = ResizeMode.NoResize };
        var open = DialogKit.Action(L.T("Open"), primary: true);
        open.Click += (_, _) => ask.DialogResult = true;
        var body = new StackPanel();
        body.Children.Add(DialogKit.Label(L.T("Password"), 0));
        body.Children.Add(password);
        DialogKit.Frame(ask, "", DialogKit.Exchange, Path.GetFileName(dialog.FileName), L.T("The password of the file, if it has one."),
            body, null, open, DialogKit.Action(L.T("Cancel"), cancel: true));
        ask.Loaded += (_, _) => password.Focus();
        if (ask.ShowDialog() != true) return;
        try
        {
            var certificate = new X509Certificate2(dialog.FileName, password.Password, X509KeyStorageFlags.EphemeralKeySet);
            if (!certificate.HasPrivateKey)
            {
                DialogKit.ShowError(_info, L.T("{0} holds no key to sign with.", Path.GetFileName(dialog.FileName)));
                return;
            }
            AddSigner(certificate, Path.GetFileName(dialog.FileName), select: true);
            DialogKit.ShowInfo(_info, "");
        }
        catch (System.Security.Cryptography.CryptographicException ex)
        {
            DialogKit.ShowError(_info, L.T("{0} does not open with this password: {1}", Path.GetFileName(dialog.FileName), ex.Message.Trim()));
        }
    }

    private void NewSigner()
    {
        var name = new TextBox { Padding = new Thickness(3) };
        var ask = new Window { Owner = this, Width = 520, SizeToContent = SizeToContent.Height, ResizeMode = ResizeMode.NoResize };
        var make = DialogKit.Action(L.T("Create"), primary: true);
        make.Click += (_, _) => { if (name.Text.Trim().Length > 0) ask.DialogResult = true; };
        var body = new StackPanel();
        body.Children.Add(DialogKit.Label(L.T("Name in the certificate"), 0));
        body.Children.Add(name);
        body.Children.Add(new TextBlock
        {
            Text = L.T("Everyone who checks a Descriptor you sign sees this name, for example your organisation or department. The certificate is self-signed and valid for three years; the key stays in your Windows profile."),
            Foreground = DialogKit.Muted, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0),
        });
        DialogKit.Frame(ask, "", DialogKit.Create, L.T("New certificate to sign with"), "", body, null, make, DialogKit.Action(L.T("Cancel"), cancel: true));
        ask.Loaded += (_, _) => name.Focus();
        if (ask.ShowDialog() != true) return;
        X509Certificate2 certificate;
        try { certificate = _keys.CreateSigner(name.Text.Trim()); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.Cryptography.CryptographicException)
        {
            DialogKit.ShowError(_info, L.T("Making the certificate failed: {0}", ex.Message));
            return;
        }
        AddSigner(certificate, L.T("kept by the plugin"), select: true);
        DialogKit.ShowInfo(_info, L.T("Made the certificate {0}.", certificate.Subject));
    }
}

/// <summary>What the Check tab says about a Descriptor that was checked.</summary>
internal static class DescriptorSummary
{
    public static string Of(DescriptorReport report)
    {
        var parts = new List<string>
        {
            report.Info is { } info
                ? L.T("Descriptor {0}, version {1}, OPC UA FX {2}.", info.Identifier, info.Version, info.OpcUaFxVersion)
                : L.T("Descriptor without manifest."),
        };
        if (report.RootFiles.Count > 0) parts.Add(L.T("Root AML file: {0}.", string.Join(", ", report.RootFiles)));
        if (report.Attachments.Count > 0) parts.Add(L.T("{0} attachment(s).", report.Attachments.Count));
        if (report.Embedded.Count > 0) parts.Add(L.T("{0} embedded Descriptor(s).", report.Embedded.Count));
        foreach (var verdict in report.Signatures)
        {
            var s = verdict.Signature;
            var state = !s.ValueValid || s.ChangedParts.Count > 0 || s.MissingParts.Count > 0 ? L.T("does not verify")
                : !verdict.Valid ? L.T("not valid, see the findings")
                : !verdict.ChainComplete ? L.T("valid, but its chain is incomplete")
                : verdict.Trusted ? L.T("valid and trusted") : L.T("valid, not trusted yet");
            parts.Add(s.Time is { } time
                ? L.T("Signed by {0} on {1:yyyy-MM-dd HH:mm}: {2}.", verdict.SignerName, time.LocalDateTime, state)
                : L.T("Signed by {0}: {1}.", verdict.SignerName, state));
        }
        return string.Join(" ", parts);
    }

    /// <summary>
    /// The signer to offer trust to: of a signature that holds in every respect but
    /// whom it comes from, whose chain is complete, and that nobody trusts yet. Trust
    /// is never offered for a signature that does not hold, weak or changed.
    /// </summary>
    public static X509Certificate2? SignerToTrust(DescriptorReport report) =>
        report.Signatures.FirstOrDefault(v => v.Valid && v.ChainComplete && !v.SignerTrusted)?.Signature.Signer;

    /// <summary>The SHA-256 fingerprint, in pairs, to compare with what the sender says.</summary>
    public static string Fingerprint(X509Certificate2 certificate) =>
        string.Join(":", Convert.ToHexString(certificate.GetCertHash(System.Security.Cryptography.HashAlgorithmName.SHA256)).Chunk(2).Select(c => new string(c)));
}
