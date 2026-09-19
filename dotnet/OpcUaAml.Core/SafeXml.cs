// XML from sources the user does not control (a server's files): no DTD, so
// no entity can expand a small file into a large one.

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
}
