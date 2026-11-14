// Large changes to a document the AutomationML Editor has open.
//
// The editor registers Aml.Engine services for the whole process: a lookup
// service with automatic reference updates on for every open document, and an
// undo service. When an element goes, the lookup service rewrites every
// reference to it, clears those whose target is gone, and updates its tables
// element by element; each rewrite passes the undo service and the editor's
// bindings. Replacing the UA base library that way took 43 s and left the
// instances typed by it without their types.

using Aml.Engine.CAEX;
using Aml.Engine.Services;
using Aml.Engine.Services.Interfaces;

namespace OpcUaAml.Import;

public static class EditorServices
{
    /// <summary>
    /// Runs <paramref name="change"/> with automatic reference updates held and
    /// the document out of the lookup service's registry, before and after, so
    /// the service builds its tables again in one pass the next time it is
    /// asked. For changes that remove and insert whole libraries or hierarchies
    /// whose paths stay the same. Without an editor's services it just runs.
    /// </summary>
    public static T Hold<T>(CAEXDocument document, Func<T> change)
    {
        var autoUpdate = ServiceLocator.AutoUpdateService;
        var registry = ServiceLocator.QueryService as IXMLDocumentRegistry;
        var held = autoUpdate?.IsAutoUpdateEnabled == true;
        if (held) autoUpdate!.IsAutoUpdateEnabled = false;
        try
        {
            registry?.RemoveDocument(document);
            return change();
        }
        finally
        {
            // The service rebuilt its tables when the first element arrived,
            // before the rest was in: references into what came later were
            // missing from them.
            registry?.RemoveDocument(document);
            // Rebuilt now, while automatic updates are still off. Rebuilt later,
            // with them on, the service reports every reference it finds as
            // updated, and the editor's tree searches itself once for each:
            // 153751 reports after importing DI, minutes without an answer.
            if (registry is LookupService lookup) lookup.AddDocument(document);
            if (held) autoUpdate!.IsAutoUpdateEnabled = true;
        }
    }

    /// <inheritdoc cref="Hold{T}(CAEXDocument, Func{T})"/>
    public static void Hold(CAEXDocument document, Action change) => Hold(document, () => { change(); return 0; });

    /// <summary>
    /// <see cref="Hold{T}(CAEXDocument, Func{T})"/>, and the editor's undo service
    /// paused for the document meanwhile: for changes made element by element,
    /// which it would record one by one. Taking 188 nodes of a server recorded
    /// 1394 changes and took 46 s in the editor, 1.7 s without it. The plugin's
    /// changes are not undone with Ctrl+Z anyway (see the user manual).
    /// </summary>
    public static T Quietly<T>(CAEXDocument document, Func<T> change)
    {
        var undo = ServiceLocator.UndoRedoService;
        undo?.Pause(document);
        try
        {
            return Hold(document, change);
        }
        finally
        {
            undo?.Resume(document);
        }
    }

    /// <inheritdoc cref="Quietly{T}(CAEXDocument, Func{T})"/>
    public static void Quietly(CAEXDocument document, Action change) => Quietly(document, () => { change(); return 0; });

    /// <summary>
    /// Runs <paramref name="change"/> with the editor's undo service paused for the
    /// document, and nothing else held: for values, which change no reference.
    /// Held like a structural change, every batch of live values made the lookup
    /// service build its tables for the whole document again.
    /// </summary>
    public static void WithoutUndo(CAEXDocument document, Action change)
    {
        // Closing, the editor unloads the document before the plugin hears of it; values
        // still on their way have nowhere to go, and the undo service throws for it.
        if (!IsLoaded(document)) return;
        var undo = ServiceLocator.UndoRedoService;
        undo?.Pause(document);
        try
        {
            change();
        }
        finally
        {
            undo?.Resume(document);
        }
    }

    /// <summary>
    /// Tells the editor of elements hung into <paramref name="document"/> by XLinq,
    /// as Aml.Engine tells it of an element appended through its API. The editor's
    /// trees follow these events, not the XML: without them a hierarchy that
    /// received a whole subtree at once looked empty there.
    /// </summary>
    public static void Announce(CAEXDocument document, IEnumerable<CAEXBasicObject> added)
    {
        if (!IsLoaded(document)) return;
        foreach (var element in added)
        {
            if (element.Node.Parent is not { } parent || element.Node.Document != document.XDocument) continue;
            Aml.Engine.CAEX.Commands.CaexCommand.OnCAEXElementChangedEvent(document.XDocument,
                new Aml.Engine.CAEX.Commands.CAEXElementChangeEventArgs(element.Node, parent, document, Aml.Engine.CAEX.Commands.CAEXElementChangeMode.Added));
        }
    }

    /// <summary>Whether <paramref name="document"/> still has its XML; an unloaded one has none.</summary>
    public static bool IsLoaded(CAEXDocument document) => document.XDocument != null;
}
