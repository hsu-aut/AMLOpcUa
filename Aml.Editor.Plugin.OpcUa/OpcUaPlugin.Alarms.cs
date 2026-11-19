// Create alarm… beside New instance… on the Namespaces tab: an alarm of OPC UA
// Part 9 for the value selected in the editor, created beside it as an
// instance of its class from the UA base library. A document without that
// library is offered to import it first, as an import of any NodeSet brings it.

using System.IO;
using System.Windows;
using Aml.Editor.Plugin.Contracts;
using Aml.Editor.Plugin.OpcUa.Bridge;
using Aml.Editor.Plugin.OpcUa.Diagnostics;
using Aml.Editor.Plugin.OpcUa.Localization;
using Aml.Engine.CAEX;
using OpcUaAml.Alarms;
using OpcUaAml.NodeSets;
using OpcUaAml.Server;

namespace Aml.Editor.Plugin.OpcUa;

public partial class OpcUaPlugin
{
    private static readonly HashSet<string> AlarmValueTypes = new(StringComparer.Ordinal)
    {
        "xs:boolean", "xs:double", "xs:float", "xs:decimal", "xs:int", "xs:integer", "xs:long", "xs:short", "xs:byte",
        "xs:unsignedInt", "xs:unsignedLong", "xs:unsignedShort", "xs:unsignedByte",
    };

    /// <summary>Asks whether to import the base library; tests answer without a window.</summary>
    internal Func<bool>? AskImportBaseLibrary { get; set; }

    /// <summary>The value selected in the editor that an alarm can watch, or null with why not.</summary>
    private (InternalElementType? Value, string Why) AlarmTarget()
    {
        if (_document == null || _editorSelection is not InternalElementType value || !ReferenceEquals(value.CAEXDocument, _document)
            || !AmlAddressSpace.IsServedVariable(value) || value.Attribute["Value"] is not { } attribute)
            return (null, L.T("Select a value in the editor first: an element with a Value."));
        if (attribute.AttributeDataType is not { } type || !AlarmValueTypes.Contains(type))
            return (null, L.T("Alarms watch numbers and booleans; '{0}' holds {1}.", value.Name, attribute.AttributeDataType ?? "xs:string"));
        return (value, L.T("Create an alarm for the value selected in the editor: a limit or state alarm of OPC UA Part 9 beside it, which the document server raises."));
    }

    /// <summary>The button follows the editor's selection.</summary>
    private void UpdateAlarmButton()
    {
        var (value, why) = AlarmTarget();
        AlarmButton.IsEnabled = value != null && !_busy && _document?.CAEXFile.SchemaVersion == OpcUaAml.Import.LibraryMerger.RequiredSchemaVersion;
        AlarmButton.ToolTip = why;
    }

    private async void AlarmButton_Click(object sender, RoutedEventArgs e)
    {
        var document = _document;
        if (document == null || _busy || AlarmTarget().Value is not { } value) return;
        if (!await EnsureBaseLibraryAsync(document)) return;
        var window = new AlarmWindow(value) { Owner = Window.GetWindow(this) };
        if (window.ShowDialog() != true || window.Request is not { } request) return;
        CreateAlarm(value, request);
    }

    /// <summary>
    /// True when the document holds the alarm classes of the UA base library,
    /// after asking to import it if it did not.
    /// </summary>
    internal async Task<bool> EnsureBaseLibraryAsync(CAEXDocument document)
    {
        if (AlarmElements.HasBaseLibrary(document)) return true;
        var ask = AskImportBaseLibrary ?? (() => DialogKit.Confirm(Window.GetWindow(this), "", DialogKit.Exchange,
            L.T("Import the OPC UA base library?"),
            L.T("Alarms are instances of classes of the OPC UA base library, which this document does not have yet. Import it now? The libraries of the base model are added; nothing else changes."),
            L.T("Import")));
        if (!ask()) return false;
        await ImportFileAsync(document, Path.Combine(NodeSetCatalog.BundledFolder, "Opc.Ua.NodeSet2.xml"), []);
        return AlarmElements.HasBaseLibrary(document);
    }

    /// <summary>Creates the alarm beside the value, selects it in the editor and says so.</summary>
    internal InternalElementType? CreateAlarm(InternalElementType value, AlarmRequest request)
    {
        try
        {
            var alarm = AlarmElements.Create(value, request.Kind, request.Limit, request.Normal, request.Severity, request.Message, request.Name);
            var message = L.T("Created alarm '{0}' beside '{1}'.", alarm.Name, value.Name);
            PluginLog.Info($"Created alarm {alarm.Name} ({request.Kind}) on {value.Name}.");
            var saved = SaveAfterImport && EditorSaver.TrySaveActiveDocument();
            message += saved ? L.T(" The editor was asked to save it.") : L.T(" Press Ctrl+S to save.");
            message += L.T(" Ctrl+Z in the editor does not take it back; delete the element instead.");
            if (_host != null) message += L.T(" The document server raises it after a restart.");
            SetStatus(message);
            Selected?.Invoke(this, new SelectionEventArgs(alarm));
            return alarm;
        }
        catch (AlarmException ex)
        {
            PluginLog.Warn($"Creating the alarm failed: {ex.Message}");
            SetStatus(ex.Problem switch
            {
                AlarmProblem.NoBaseLibrary => L.T("The document has no OPC UA base library."),
                AlarmProblem.NotAValue => L.T("Select a value in the editor first: an element with a Value."),
                AlarmProblem.NotANumber => L.T("'{0}' is no number; a limit alarm watches a number.", value.Name),
                AlarmProblem.NotBoolean => L.T("'{0}' is no boolean; a state alarm watches true or false.", value.Name),
                AlarmProblem.NameTaken => L.T("'{0}' is taken beside '{1}'.", request.Name, value.Name),
                _ => L.T("A severity is 1 to 1000."),
            });
            return null;
        }
    }
}
