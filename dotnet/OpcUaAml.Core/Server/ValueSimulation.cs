// How a served value moves when a server of this build simulates a running
// plant: numbers swing on a slow sine around the value the model gives them,
// each variable in its own phase, booleans toggle every few seconds, texts
// stay. Both servers use it, so a document and a NodeSet behave alike.

using System.Globalization;
using Opc.Ua;

namespace OpcUaAml.Server;

internal static class ValueSimulation
{
    /// <summary>
    /// The next value of a variable, or null when its type does not move.
    /// </summary>
    /// <param name="dataType">The variable's DataType.</param>
    /// <param name="middle">The value the model gives it, which it swings around.</param>
    /// <param name="seconds">Seconds since the server started.</param>
    /// <param name="phase">The variable's own phase, so not everything moves together.</param>
    public static object? Next(NodeId dataType, object? middle, double seconds, double phase)
    {
        if (dataType == DataTypeIds.Boolean)
            return ((int)((seconds + phase * 3) / 5)) % 2 == 0 ? middle as bool? ?? false : !(middle as bool? ?? false);
        double center;
        try { center = middle == null ? 0 : Convert.ToDouble(middle, CultureInfo.InvariantCulture); }
        catch (Exception ex) when (ex is InvalidCastException or FormatException) { return null; }
        var amplitude = center == 0 ? 10 : Math.Abs(center) * 0.2;
        var x = center + amplitude * Math.Sin(2 * Math.PI * seconds / 30 + phase);
        if (dataType == DataTypeIds.Double) return Math.Round(x, 3);
        if (dataType == DataTypeIds.Float) return (float)Math.Round(x, 3);
        if (dataType == DataTypeIds.Int16) return (short)Math.Clamp(Math.Round(x), short.MinValue, short.MaxValue);
        if (dataType == DataTypeIds.Int32) return (int)Math.Clamp(Math.Round(x), int.MinValue, int.MaxValue);
        if (dataType == DataTypeIds.Int64) return (long)Math.Round(x);
        if (dataType == DataTypeIds.UInt16) return (ushort)Math.Clamp(Math.Round(x), 0, ushort.MaxValue);
        if (dataType == DataTypeIds.UInt32) return (uint)Math.Clamp(Math.Round(x), 0, uint.MaxValue);
        if (dataType == DataTypeIds.Byte) return (byte)Math.Clamp(Math.Round(x), 0, byte.MaxValue);
        return null;
    }
}
