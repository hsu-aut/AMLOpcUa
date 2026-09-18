// What a NodeSet file declares about itself: the models it defines and the
// models it needs. Read with a streaming reader that stops at </Models>, so a
// folder of large NodeSets can be scanned without parsing any of their nodes.

using System.Xml;

namespace OpcUaAml.NodeSets;

/// <summary>A model reference as it appears in a NodeSet's Models section.</summary>
public sealed record ModelRef(string ModelUri, string? Version, DateTime? PublicationDate);

/// <summary>One model defined by a NodeSet file.</summary>
public sealed record ModelDeclaration(ModelRef Model, IReadOnlyList<ModelRef> RequiredModels);

/// <summary>The header of a NodeSet file.</summary>
public sealed record NodeSetInfo(string FilePath, IReadOnlyList<ModelDeclaration> Models)
{
    /// <summary>The first declared model; NodeSets in practice declare exactly one.</summary>
    public ModelDeclaration PrimaryModel => Models.Count > 0
        ? Models[0]
        : throw new InvalidOperationException($"'{FilePath}' declares no model.");

    public const string UaNamespace = "http://opcfoundation.org/UA/";

    private const string NodeSetNamespace = "http://opcfoundation.org/UA/2011/03/UANodeSet.xsd";

    /// <summary>
    /// Reads the Models section. Returns null when the file is not a UANodeSet,
    /// so a folder scan can skip unrelated XML files instead of failing on them.
    /// </summary>
    public static NodeSetInfo? TryRead(string filePath)
    {
        try
        {
            return Read(filePath);
        }
        catch (Exception ex) when (ex is XmlException or InvalidDataException)
        {
            return null;
        }
    }

    /// <summary>Reads the Models section of a NodeSet file.</summary>
    public static NodeSetInfo Read(string filePath)
    {
        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            IgnoreComments = true,
            IgnoreWhitespace = true,
        };

        using var reader = XmlReader.Create(filePath, settings);
        if (!reader.ReadToFollowing("UANodeSet", NodeSetNamespace))
            throw new InvalidDataException($"'{filePath}' is not a UANodeSet.");

        var models = new List<ModelDeclaration>();
        ModelRef? current = null;
        var required = new List<ModelRef>();

        while (reader.Read())
        {
            if (reader.NodeType == XmlNodeType.Element && reader.NamespaceURI == NodeSetNamespace)
            {
                switch (reader.LocalName)
                {
                    case "Model":
                        if (current != null) models.Add(new ModelDeclaration(current, required));
                        current = ReadRef(reader);
                        required = new List<ModelRef>();
                        if (reader.IsEmptyElement)
                        {
                            models.Add(new ModelDeclaration(current, required));
                            current = null;
                        }
                        break;
                    case "RequiredModel":
                        required.Add(ReadRef(reader));
                        break;
                    case "Aliases":
                    case "UAObject":
                    case "UAObjectType":
                    case "UAVariable":
                    case "UAVariableType":
                    case "UADataType":
                    case "UAReferenceType":
                    case "UAMethod":
                    case "UAView":
                        // Past the header; nothing more to learn.
                        if (current != null) models.Add(new ModelDeclaration(current, required));
                        return new NodeSetInfo(filePath, models);
                }
            }
            else if (reader.NodeType == XmlNodeType.EndElement && reader.LocalName == "Model" && current != null)
            {
                models.Add(new ModelDeclaration(current, required));
                current = null;
            }
            else if (reader.NodeType == XmlNodeType.EndElement && reader.LocalName == "Models")
            {
                break;
            }
        }

        if (current != null) models.Add(new ModelDeclaration(current, required));
        return new NodeSetInfo(filePath, models);
    }

    private static ModelRef ReadRef(XmlReader reader)
    {
        var uri = reader.GetAttribute("ModelUri")
            ?? throw new InvalidDataException("A Model or RequiredModel element has no ModelUri.");
        DateTime? date = null;
        var rawDate = reader.GetAttribute("PublicationDate");
        if (rawDate != null && DateTime.TryParse(rawDate, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal,
                out var parsed))
        {
            date = parsed;
        }
        return new ModelRef(uri, reader.GetAttribute("Version"), date);
    }
}
