// AML to OPC UA: a CAEX document (2.15 or 3.0) as a UANodeSet.
//
// The rules are those of the joint working group of AutomationML e.V. and the
// OPC Foundation, published as XSLT 2.0 in AutomationML/AML-UA-XSLT. They
// revise OPC 30040 "OPC UA for AutomationML" v1.00 (2016). .NET has no XSLT 2.0
// processor, so AmlUaXsltTranslator ports the stylesheets template by
// template. The XSLT has bugs; by default a few evident ones are fixed (listed
// in docs/export.md), XsltCompatibility reproduces its output exactly.

using System.Xml;
using System.Xml.Linq;
using Aml.Engine.CAEX;
using OpcUaAml.Import;

namespace OpcUaAml.Export;

/// <summary>The rule set an export follows.</summary>
public enum ExportMode
{
    /// <summary>
    /// The current rules of the AutomationML/OPC Foundation working group
    /// (AML-UA-XSLT, commit a144dcc). OPC 30040 v1.00 is described in
    /// docs/export.md but not implemented.
    /// </summary>
    AmlUaXslt,

    /// <summary>
    /// The inverse of OPC 10000-83 Annex A, for libraries Annex A generated: the
    /// namespace's types, DataTypes, ReferenceTypes and objects as the OPC UA
    /// nodes they were (<see cref="AnnexAInverse"/>). Not a standard.
    /// </summary>
    AnnexAInverse,
}

public sealed class NodeSetExportOptions
{
    public ExportMode Mode { get; init; } = ExportMode.AmlUaXslt;

    /// <summary>
    /// PublicationDate written to every Model and RequiredModel of the file's
    /// own libraries. Defaults to the current time; set it for reproducible output.
    /// </summary>
    public DateTime? PublicationDate { get; init; }

    /// <summary>
    /// Reproduce the XSLT output including its bugs, for conformance tests.
    /// Off, the deviations listed in docs/export.md apply.
    /// </summary>
    public bool XsltCompatibility { get; init; }

    /// <summary>Write section comments into the NodeSet.</summary>
    public bool Comments { get; init; } = true;

    /// <summary>
    /// For <see cref="ExportMode.AnnexAInverse"/>: the namespace to export. When
    /// not given, the document must hold the Annex A libraries of exactly one
    /// namespace besides those of the UA base model.
    /// </summary>
    public string? NamespaceUri { get; init; }
}

public static class NodeSetExporter
{
    public const string UaNodeSetNamespace = "http://opcfoundation.org/UA/2011/03/UANodeSet.xsd";
    public const string UaTypesNamespace = "http://opcfoundation.org/UA/2008/02/Types.xsd";

    /// <summary>Exports the CAEX XML of a document as a UANodeSet.</summary>
    public static XDocument Export(XDocument caex, NodeSetExportOptions? options = null)
    {
        var root = caex.Root ?? throw new ArgumentException("The document has no root element.", nameof(caex));
        if (root.Name.LocalName != "CAEXFile")
            throw new ArgumentException($"Expected a CAEXFile root element, found '{root.Name.LocalName}'.", nameof(caex));
        options ??= new NodeSetExportOptions();
        return options.Mode switch
        {
            ExportMode.AmlUaXslt => new AmlUaXsltTranslator(root, options).Translate(),
            ExportMode.AnnexAInverse => AnnexAInverse.Export(caex, new AnnexAInverseOptions
            {
                NamespaceUri = options.NamespaceUri ?? AnnexAInverse.SingleNamespace(root),
                PublicationDate = options.PublicationDate,
            }),
            _ => throw new NotSupportedException($"Export mode {options.Mode} is not implemented."),
        };
    }

    /// <summary>Exports an Aml.Engine document. The document is not changed.</summary>
    public static XDocument Export(CAEXDocument document, NodeSetExportOptions? options = null)
    {
        var root = document.CAEXFile.Node;
        return Export(root.Document ?? new XDocument(root), options);
    }

    /// <summary>Exports an .aml file, or the root document of an .amlx container.</summary>
    public static XDocument ExportFile(string amlPath, NodeSetExportOptions? options = null)
    {
        if (!File.Exists(amlPath)) throw new FileNotFoundException($"'{amlPath}' does not exist.", amlPath);
        if (Path.GetExtension(amlPath).Equals(".amlx", StringComparison.OrdinalIgnoreCase))
            return Export(NodeSetImporter.ReadContainer(amlPath), options);
        // Loaded as plain XML: whitespace and attribute order as written, like
        // the XSLT processor sees them.
        return Export(XDocument.Load(amlPath, LoadOptions.PreserveWhitespace), options);
    }

    /// <summary>Exports <paramref name="amlPath"/> and writes the NodeSet to <paramref name="nodeSetPath"/>.</summary>
    public static void ExportFile(string amlPath, string nodeSetPath, NodeSetExportOptions? options = null)
    {
        var nodeSet = ExportFile(amlPath, options);
        using var writer = XmlWriter.Create(nodeSetPath, WriterSettings);
        nodeSet.Save(writer);
    }

    /// <summary>The NodeSet as indented UTF-8 XML text.</summary>
    public static string ToXml(XDocument nodeSet)
    {
        using var stream = new MemoryStream();
        using (var writer = XmlWriter.Create(stream, WriterSettings)) nodeSet.Save(writer);
        return new System.Text.UTF8Encoding(false).GetString(stream.ToArray());
    }

    private static readonly XmlWriterSettings WriterSettings = new()
    {
        Indent = true,
        IndentChars = "  ",
        Encoding = new System.Text.UTF8Encoding(false),
    };
}
