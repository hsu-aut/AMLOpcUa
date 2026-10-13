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
            if (held) autoUpdate!.IsAutoUpdateEnabled = true;
        }
    }

    /// <inheritdoc cref="Hold{T}(CAEXDocument, Func{T})"/>
    public static void Hold(CAEXDocument document, Action change) => Hold(document, () => { change(); return 0; });
}
