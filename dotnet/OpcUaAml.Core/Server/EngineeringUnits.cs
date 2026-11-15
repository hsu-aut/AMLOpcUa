// The unit of a value as OPC UA states it: EUInformation after UNECE
// Recommendation 20 (OPC 10000-8, EngineeringUnits), found for the unit CAEX
// gives an attribute. The table is the OPC Foundation's UNECE_to_OPCUA.csv
// (UA-Nodeset, Schema), embedded unchanged.

using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace OpcUaAml.Server;

/// <summary>An engineering unit: UNECE code, the UnitId OPC UA derives from it, symbol and name.</summary>
public sealed record EngineeringUnit(string? NamespaceUri, int UnitId, string DisplayName, string Description, string? Code)
{
    /// <summary>The namespace of the UNECE units in OPC UA.</summary>
    public const string UneceNamespace = "http://www.opcfoundation.org/UA/units/un/cefact";

    /// <summary>
    /// The UNECE unit a CAEX unit names: by its symbol ("°C", "mm"; case
    /// matters, "A" is the ampere and "a" the are), by a usual plain-text
    /// spelling ("1/min", "rpm", "m2", "Nm"), by its common code ("CEL") or by
    /// its name ("degree Celsius"). Any other text stays as it is, with the
    /// UnitId -1 OPC 10000-8 gives a unit without one and no namespace.
    /// </summary>
    public static EngineeringUnit From(string unit)
    {
        var text = unit.Trim();
        var table = Table.Value;
        if (table.BySymbol.TryGetValue(text, out var found)) return found;
        if (Spellings.TryGetValue(text, out var code) && table.ByCode.TryGetValue(code, out found)) return found;
        var written = Superscripts(text);
        if (table.BySymbol.TryGetValue(written, out found)) return found;
        if (table.ByCode.TryGetValue(text, out found)) return found;
        if (table.ByName.TryGetValue(text, out found)) return found;
        return new EngineeringUnit(null, -1, text, "", null);
    }

    /// <summary>
    /// A unit as a server states it (EUInformation), with its UNECE code where
    /// the namespace is UNECE's and the UnitId is in the table.
    /// </summary>
    public static EngineeringUnit Of(string? namespaceUri, int unitId, string? displayName, string? description)
    {
        string? code = null;
        if (namespaceUri == UneceNamespace && Table.Value.ById.TryGetValue(unitId, out var known)) code = known.Code;
        return new EngineeringUnit(namespaceUri, unitId, displayName ?? "", description ?? "", code);
    }

    /// <summary>Plain-text spellings of units whose symbol holds characters a keyboard lacks.</summary>
    private static readonly Dictionary<string, string> Spellings = new(StringComparer.Ordinal)
    {
        ["1/min"] = "C94", ["1/s"] = "C97", ["rpm"] = "RPM", ["U/min"] = "RPM",
        ["degC"] = "CEL", ["deg"] = "DD", ["Nm"] = "NU", ["%"] = "P1", ["pct"] = "P1", ["kWh"] = "KWH",
    };

    /// <summary>"m2", "m^2" as "m²", "s^-1" as "s⁻¹", "N*m" as "N·m".</summary>
    private static string Superscripts(string text)
    {
        var s = text.Replace("^-1", "⁻¹").Replace("^-2", "⁻²").Replace("^2", "²").Replace("^3", "³").Replace('*', '·');
        return Regex.Replace(s, @"(?<=[A-Za-zµ])([23])(?![0-9])", m => m.Value == "2" ? "²" : "³");
    }

    private sealed record Units(
        Dictionary<string, EngineeringUnit> BySymbol,
        Dictionary<string, EngineeringUnit> ByCode,
        Dictionary<string, EngineeringUnit> ByName,
        Dictionary<int, EngineeringUnit> ById);

    private static readonly Lazy<Units> Table = new(Load);

    /// <summary>The table, symbols and names taken by their first row where two share one.</summary>
    private static Units Load()
    {
        var bySymbol = new Dictionary<string, EngineeringUnit>(StringComparer.Ordinal);
        var byCode = new Dictionary<string, EngineeringUnit>(StringComparer.Ordinal);
        var byName = new Dictionary<string, EngineeringUnit>(StringComparer.OrdinalIgnoreCase);
        var byId = new Dictionary<int, EngineeringUnit>();
        using var stream = typeof(EngineeringUnit).Assembly.GetManifestResourceStream("OpcUaAml.units.UNECE_to_OPCUA.csv")
            ?? throw new InvalidOperationException("The UNECE unit table is not embedded.");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        reader.ReadLine(); // UNECECode,UnitId,DisplayName,Description
        while (reader.ReadLine() is { } line)
        {
            var f = Fields(line);
            if (f.Count < 4 || !int.TryParse(f[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var id)) continue;
            var unit = new EngineeringUnit(UneceNamespace, id, f[2], f[3], f[0]);
            byCode.TryAdd(f[0], unit);
            bySymbol.TryAdd(f[2], unit);
            byName.TryAdd(f[3], unit);
            byId.TryAdd(id, unit);
        }
        return new Units(bySymbol, byCode, byName, byId);
    }

    /// <summary>The fields of a CSV line, quoted ones with their commas and doubled quotes.</summary>
    private static List<string> Fields(string line)
    {
        var fields = new List<string>();
        var field = new StringBuilder();
        var quoted = false;
        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (quoted)
            {
                if (c == '"' && i + 1 < line.Length && line[i + 1] == '"') { field.Append('"'); i++; }
                else if (c == '"') quoted = false;
                else field.Append(c);
            }
            else if (c == '"') quoted = true;
            else if (c == ',') { fields.Add(field.ToString()); field.Clear(); }
            else field.Append(c);
        }
        fields.Add(field.ToString());
        return fields;
    }
}
