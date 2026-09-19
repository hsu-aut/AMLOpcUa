// CAEX 2.15 documents (AutomationML 2.0) to CAEX 3.0, which the libraries of
// OPC 10000-83 Annex A need (AttributeTypeLib, nested attribute types). The
// conversion is Aml.Engine's own (Aml.Engine.Services, CAEXSchemaTransformer);
// it gives a new document, the original stays as it was.

using Aml.Engine.CAEX;
using Aml.Engine.Services;

namespace OpcUaAml.Import;

public static class CaexUpgrade
{
    private static readonly object Gate = new();

    /// <summary>Whether the document must be converted before OPC UA libraries can go into it.</summary>
    public static bool IsNeeded(CAEXDocument document) =>
        document.CAEXFile.SchemaVersion != LibraryMerger.RequiredSchemaVersion;

    /// <summary>A CAEX 3.0 copy of the document; the document itself when it is CAEX 3.0 already.</summary>
    public static CAEXDocument ToCaex3(CAEXDocument document)
    {
        if (!IsNeeded(document)) return document;
        // The transformer is a service of the engine's global ServiceLocator.
        lock (Gate)
        {
            var transformer = CAEXSchemaTransformer.Register();
            try
            {
                return transformer.TransformTo(document, CAEXDocument.CAEXSchema.CAEX3_0);
            }
            finally
            {
                CAEXSchemaTransformer.UnRegister();
            }
        }
    }
}
