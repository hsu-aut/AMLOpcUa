// The OPC UA stack starts every message context from one factory of message
// types, filled with those of the first Opc.Ua.Core in the process: it looks
// through all assemblies of the AppDomain, across load contexts
// (CoreUtils.GetOpcUaCoreAssembly, stack 1.5.378). In the AML Editor every
// plugin has a load context of its own, and another plugin may have loaded an
// Opc.Ua.Core of its own before (MTP Control brings 1.5.377). This library's
// stack then knew no message type and read no server's answer.

using System.Reflection;
using Opc.Ua;

namespace OpcUaAml.Server;

public static class OpcUaStack
{
    /// <summary>
    /// Makes the stack use the message types of this library's own
    /// Opc.Ua.Core, also when another one was loaded first. Cheap once they
    /// are in place. True when they are.
    /// </summary>
    public static bool UseOwnTypes()
    {
        if (HasOwnTypes()) return true;
        // The factory every message context starts from (EncodeableFactory.Create clones it) is not public.
        if (typeof(EncodeableFactory).GetProperty("Root", BindingFlags.NonPublic | BindingFlags.Static)?.GetValue(null)
            is not EncodeableFactory root)
            return false;
        root.Builder.AddEncodeableTypes(typeof(OpenSecureChannelResponse).Assembly).Commit();
        return HasOwnTypes();
    }

    private static bool HasOwnTypes() =>
        EncodeableFactory.Create().TryGetEncodeableType(ObjectIds.OpenSecureChannelResponse_Encoding_DefaultBinary, out var type)
        && type.Type == typeof(OpenSecureChannelResponse);
}
