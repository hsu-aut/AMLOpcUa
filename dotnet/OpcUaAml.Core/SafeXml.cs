// XML without DTDs, so no entity can expand a small file into a large one:
// for a server's files and for every file the library reads itself.

using System.Xml;
using System.Xml.Linq;

namespace OpcUaAml;

public static class SafeXml
{
    public static XDocument Load(Stream stream, LoadOptions options = LoadOptions.None)
    {
        using var reader = XmlReader.Create(stream, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
        return XDocument.Load(reader, options);
    }

    public static XDocument Load(string path, LoadOptions options = LoadOptions.None)
    {
        using var stream = File.OpenRead(path);
        return Load(stream, options);
    }
}
