// UAFX Descriptors (OPC 10000-83): the document saved as one, signed, and a
// Descriptor received checked, with its findings in the Check tab. A signer
// nobody trusts yet can be trusted there after comparing its fingerprint.

using System.IO;
using System.Windows;
using Aml.Editor.Plugin.OpcUa.Diagnostics;
using Aml.Editor.Plugin.OpcUa.Localization;
using Microsoft.Win32;
using OpcUaAml.Checks;
using OpcUaAml.Descriptors;

namespace Aml.Editor.Plugin.OpcUa;

public partial class OpcUaPlugin
{
    /// <summary>The own signing keys and the trusted certificates; the tests give it a folder of their own.</summary>
    internal static DescriptorKeys DescriptorKeys { get; set; } = new();

    private DescriptorReport? _descriptorReport;
    private string? _descriptorPath;

    /// <summary>More ▾ on the Namespaces tab; it holds the Descriptor commands, which follow the document as the menu opens.</summary>
    private void NamespacesMoreButton_Click(object sender, RoutedEventArgs e)
    {
        SaveDescriptorItem.IsEnabled = _document != null && !_busy;
        CheckDescriptorItem.IsEnabled = !_busy;
        OpenMenu(NamespacesMoreMenu, NamespacesMoreButton);
    }

    private async void SaveDescriptor_Click(object sender, RoutedEventArgs e)
    {
        var document = _document;
        if (document == null || _busy) return;
        byte[] root;
        using (var stream = document.SaveToStream(true))
        using (var copy = new MemoryStream())
        {
            stream.CopyTo(copy);
            root = copy.ToArray();
        }
        var name = Path.GetFileNameWithoutExtension(_documentPath ?? document.CAEXFile.FileName ?? "document");
        if (string.IsNullOrEmpty(name)) name = "document";
        var window = new DescriptorWindow(name, root, DescriptorKeys) { Owner = Window.GetWindow(this) };
        if (window.ShowDialog() != true || window.Info is not { } info || window.Signer is not { } signer) return;
        var dialog = new SaveFileDialog
        {
            Title = L.T("Save as UAFX Descriptor"),
            Filter = L.T("UAFX Descriptor (*.amlx)|*.amlx"),
            FileName = name + ".descriptor.amlx",
            InitialDirectory = _documentPath != null ? Path.GetDirectoryName(_documentPath) : null,
        };
        if (dialog.ShowDialog() != true) return;
        if (_documentPath != null && string.Equals(Path.GetFullPath(dialog.FileName), Path.GetFullPath(_documentPath), StringComparison.OrdinalIgnoreCase))
        {
            SetStatus(L.T("That is the open document; choose another name for the Descriptor."));
            return;
        }

        SetBusy(true, L.T("Writing the Descriptor …"));
        try
        {
            var path = dialog.FileName;
            var attachments = window.Attachments.Select(p => new DescriptorFile(Path.GetFileName(p), File.ReadAllBytes(p))).ToList();
            var content = new DescriptorContent { Info = info, Root = new DescriptorFile(name + ".aml", root), Attachments = attachments };
            var keys = DescriptorKeys;
            var report = await Task.Run(() =>
            {
                DescriptorWriter.Write(path, content, signer, DateTimeOffset.Now);
                return DescriptorChecker.Check(path, keys.Trust());
            });
            ShowDescriptorReport(path, report);
            PluginLog.Info($"Saved the Descriptor {info.Identifier} {info.Version} to {path}, signed by {signer.Subject}.");
            SetStatus(L.T("Saved the Descriptor {0}, signed by {1}.", Path.GetFileName(path), signer.GetNameInfo(System.Security.Cryptography.X509Certificates.X509NameType.SimpleName, false)));
        }
        catch (Exception ex)
        {
            PluginLog.Error("Saving the Descriptor failed", ex);
            SetStatus(L.T("Saving the Descriptor failed: {0}", ex.Message));
        }
        finally
        {
            SetBusy(false, null);
        }
    }

    private async void CheckDescriptor_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        var dialog = new OpenFileDialog
        {
            Title = L.T("Check a UAFX Descriptor"),
            Filter = L.T("UAFX Descriptor (*.amlx)|*.amlx|All files (*.*)|*.*"),
        };
        if (dialog.ShowDialog() != true) return;
        await CheckDescriptorAsync(dialog.FileName);
    }

    private async Task CheckDescriptorAsync(string path)
    {
        SetBusy(true, L.T("Checking the Descriptor …"));
        try
        {
            var keys = DescriptorKeys;
            var report = await Task.Run(() => DescriptorChecker.Check(path, keys.Trust()));
            ShowDescriptorReport(path, report);
        }
        catch (Exception ex)
        {
            PluginLog.Error("Checking the Descriptor failed", ex);
            SetStatus(L.T("Checking the Descriptor failed: {0}", ex.Message));
        }
        finally
        {
            SetBusy(false, null);
        }
    }

    private void ShowDescriptorReport(string path, DescriptorReport report)
    {
        _descriptorPath = path;
        _descriptorReport = report;
        var findings = report.Findings;
        var errors = findings.Count(f => f.Severity == Severity.Error);
        FindingList.ItemsSource = findings.Select(f => new FindingRow(f)).ToList();
        CheckTab.Header = findings.Count == 0 ? L.T("Check") : L.T("Check ({0})", findings.Count);
        var summary = Path.GetFileName(path) + ": " + DescriptorSummary.Of(report) + " "
                      + L.T("{0} error(s), {1} warning(s).", errors, findings.Count - errors);
        if (findings.Count > 0)
            summary += " " + L.T("Rules: ") + string.Join("; ", findings.Select(f => f.Rule).Distinct().OrderBy(r => r)
                .Select(r => $"{r} {L.T(DescriptorRules.Descriptions[r])}"));
        CheckSummary.Text = summary;
        TrustSignerButton.Visibility = DescriptorSummary.SignerToTrust(report) != null ? Visibility.Visible : Visibility.Collapsed;
        Tabs.SelectedItem = CheckTab;
        PluginLog.Info($"Descriptor check of {path}: {errors} error(s), {findings.Count - errors} warning(s).");
        foreach (var f in findings) PluginLog.Info("  " + f);
        SetStatus(L.T("Descriptor check: {0} error(s), {1} warning(s).", errors, findings.Count - errors));
    }

    private async void TrustSignerButton_Click(object sender, RoutedEventArgs e)
    {
        if (_descriptorReport == null || _descriptorPath == null || DescriptorSummary.SignerToTrust(_descriptorReport) is not { } signer) return;
        var name = signer.GetNameInfo(System.Security.Cryptography.X509Certificates.X509NameType.SimpleName, false);
        var trust = DialogKit.Confirm(Window.GetWindow(this), "", DialogKit.Verify, L.T("Trust {0}?", name),
            L.T("Descriptors this certificate signs count as trusted from now on. Compare the fingerprint with the one the sender tells you another way, by phone or in person: a Descriptor anyone signed carries a certificate of any name."),
            L.T("Trust"),
            DialogKit.Facts(
                (L.T("Certificate"), signer.Subject, false),
                (L.T("Issued by"), signer.Issuer, false),
                (L.T("Valid"), L.T("{0:yyyy-MM-dd} to {1:yyyy-MM-dd}", signer.NotBefore, signer.NotAfter), false),
                (L.T("SHA-256 fingerprint"), DescriptorSummary.Fingerprint(signer), true)));
        if (!trust) return;
        DescriptorKeys.Trust(signer);
        PluginLog.Info($"Trusted {signer.Subject} ({signer.Thumbprint}) for Descriptors.");
        await CheckDescriptorAsync(_descriptorPath);
    }
}
