// IDs for the elements Aml.Engine copies while Opc2Aml builds the libraries.
//
// Every class instance Opc2Aml creates is a copy, and Aml.Engine's MakeUnique
// asks the ID service for a new ID for every element of the copy, whether the
// element has an ID or not: each Attribute and Value costs a Guid.NewGuid and
// its formatting. That is a sixth of a conversion. While a conversion runs,
// this service answers those elements with nothing (the ID is not used) and
// the others with a GUID from a random base and a counter.

using System.Xml.Linq;
using Aml.Engine.CAEX;
using Aml.Engine.Services;
using Aml.Engine.Services.Interfaces;

namespace OpcUaAml.Import;

internal sealed class ConversionIds : ICaexIDService
{
    // CAEX elements that never carry an ID.
    private static readonly HashSet<string> WithoutId = new(StringComparer.Ordinal)
    {
        "Attribute", "Value", "DefaultValue", "Description", "Version", "Revision", "Copyright",
        "AdditionalInformation", "Constraint", "RequiredValue", "NominalScaledType", "OrdinalScaledType",
        "UnknownType", "RefSemantic", "RequiredMaxValue", "RequiredMinValue", "Requirements",
    };

    private static readonly object Gate = new();
    private static int _users;
    private static ICaexIDService? _previous;

    private readonly byte[] _base = Guid.NewGuid().ToByteArray();
    private long _counter;

    private static readonly ConversionIds Instance = new();

    /// <summary>Uses this service until the returned scope is disposed; nested and parallel scopes share it.</summary>
    public static IDisposable Use()
    {
        lock (Gate)
        {
            if (_users++ == 0)
            {
                _previous = ServiceLocator.IDService;
                ServiceLocator.Register<ICaexIDService>(Instance);
            }
        }
        return new Scope();
    }

    private sealed class Scope : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            lock (Gate)
            {
                if (--_users == 0 && _previous != null) ServiceLocator.Register(_previous);
            }
        }
    }

    public string GenerateNewID(CAEXObject caexObject) => GenerateNewID();

    public string GenerateNewID(XElement caexElement) =>
        caexElement.Attribute("ID") == null && WithoutId.Contains(caexElement.Name.LocalName) ? "" : GenerateNewID();

    public string GenerateNewID()
    {
        var bytes = (byte[])_base.Clone();
        var n = Interlocked.Increment(ref _counter);
        for (var i = 0; i < 8; i++) bytes[8 + i] ^= (byte)(n >> (8 * i));
        return new Guid(bytes).ToString("D");
    }
}
