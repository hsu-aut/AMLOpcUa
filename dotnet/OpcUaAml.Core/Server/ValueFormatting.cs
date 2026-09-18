// Values as AML attributes hold them: text in the invariant culture, the
// lexical forms of XML Schema (true/false, ISO 8601 dates).

using System.Collections;
using System.Globalization;
using System.Xml;

namespace OpcUaAml.Server;

public static class ValueFormatting
{
    public static string? ToAml(object? value) => value switch
    {
        null => null,
        string s => s,
        bool b => b ? "true" : "false",
        DateTime d => XmlConvert.ToString(d, XmlDateTimeSerializationMode.RoundtripKind),
        float f => f.ToString("R", CultureInfo.InvariantCulture),
        double d => d.ToString("R", CultureInfo.InvariantCulture),
        byte[] bytes => Convert.ToBase64String(bytes),
        Opc.Ua.LocalizedText t => t.Text,
        Opc.Ua.QualifiedName q => q.Name,
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        IEnumerable e => string.Join(" ", e.Cast<object?>().Select(ToAml)),
        _ => value.ToString(),
    };
}
