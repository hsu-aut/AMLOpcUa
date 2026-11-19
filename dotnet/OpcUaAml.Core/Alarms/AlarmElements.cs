// Alarms in a document, as OPC 10000-9 describes them and OPC 10000-83 Annex A
// writes them: an element that is an instance of an alarm class of the UA base
// library. This reads and creates the two the servers raise: a limit alarm
// (ExclusiveLevelAlarmType with HighLimit or LowLimit) and a state alarm
// (OffNormalAlarmType, whose NormalState names a property NormalValue).
//
// InputNode and NormalState are properties of DataType NodeId. An element of
// one's own model has no NodeId yet (the document server serves it by its
// path, an export gives it a new one), so the property names its element by
// ID as well: an attribute refNode of the AutomationML object reference types.
// That stays when the element is renamed or moved; the NodeId is filled in
// where the element is served or exported. Where the element has a NodeId of
// its own, it is in the property's Value too.

using System.Globalization;
using System.Net;
using Aml.Engine.CAEX;
using Aml.Engine.CAEX.Extensions;
using OpcUaAml.Addressing;
using OpcUaAml.Links;
using OpcUaAml.Server;
using OpcUaAml.Types;

namespace OpcUaAml.Alarms;

/// <summary>Why an alarm cannot be created.</summary>
public enum AlarmProblem { NoBaseLibrary, NotAValue, NotANumber, NotBoolean, NameTaken, BadSeverity }

public sealed class AlarmException(AlarmProblem problem, string message) : Exception(message)
{
    public AlarmProblem Problem { get; } = problem;
}

/// <summary>
/// An alarm element as read: its kind (null for a class the servers do not
/// raise), the value it watches, its limit or normal value, severity and message.
/// </summary>
public sealed record AlarmElement(InternalElementType Element, AlarmKind? Kind, string ClassName, InternalElementType? Input,
    double? Limit, bool? Normal, ushort Severity, string Message);

public static class AlarmElements
{
    public const string RefNode = Vdi3682Links.RefNode;

    private const string UaLibrary = "SUC_http://opcfoundation.org/UA/";
    private const string ExclusiveLevel = "ExclusiveLevelAlarmType";
    private const string OffNormal = "OffNormalAlarmType";

    private static readonly HashSet<string> Numbers = new(StringComparer.Ordinal)
    {
        "xs:double", "xs:float", "xs:decimal", "xs:int", "xs:integer", "xs:long", "xs:short", "xs:byte",
        "xs:unsignedInt", "xs:unsignedLong", "xs:unsignedShort", "xs:unsignedByte",
    };

    /// <summary>Whether the element is an instance of AlarmConditionType or a subtype of it.</summary>
    public static bool IsAlarm(InternalElementType ie) => ChainOf(ie).Any(t => IsUa(t, "AlarmConditionType"));

    /// <summary>Whether the document holds the base library's classes for the alarms the servers raise.</summary>
    public static bool HasBaseLibrary(CAEXDocument doc) => Class(doc, ExclusiveLevel) != null && Class(doc, OffNormal) != null;

    public static IReadOnlyList<AlarmElement> In(CAEXDocument doc) =>
        doc.CAEXFile.InstanceHierarchy.SelectMany(ih => ih.Descendants<InternalElementType>()).Where(IsAlarm).Select(Read).ToList();

    public static AlarmElement Read(InternalElementType ie)
    {
        var chain = ChainOf(ie).ToList();
        var className = chain.FirstOrDefault()?.Name ?? "";
        AlarmKind? kind = null;
        double? limit = null;
        bool? normal = null;
        if (chain.Any(t => IsUa(t, ExclusiveLevel)))
        {
            var high = ie.InternalElement["HighLimit"];
            var low = ie.InternalElement["LowLimit"];
            kind = high != null ? AlarmKind.High : low != null ? AlarmKind.Low : null;
            limit = Number(ValueOf(high ?? low));
        }
        else if (chain.Any(t => IsUa(t, OffNormal)))
        {
            kind = AlarmKind.State;
            var named = Referenced(ie.InternalElement["NormalState"]) ?? ie.InternalElement["NormalValue"];
            normal = ValueOf(named) switch { "true" or "1" => true, "false" or "0" => false, _ => null };
        }
        var severity = ushort.TryParse(ValueOf(ie.InternalElement["Severity"]), NumberStyles.Integer, CultureInfo.InvariantCulture, out var s) ? s : (ushort)0;
        return new AlarmElement(ie, kind, className, Referenced(ie.InternalElement["InputNode"]), limit, normal, severity,
            ValueOf(ie.InternalElement["Message"]) ?? "");
    }

    /// <summary>
    /// What makes the alarm unservable, in English for the log; null when the
    /// servers can raise it.
    /// </summary>
    public static string? Problem(AlarmElement alarm) =>
        alarm.Kind == null ? $"'{alarm.Element.Name}' is a {alarm.ClassName}; the servers raise limit and state alarms only (ExclusiveLevelAlarmType, OffNormalAlarmType)."
        : alarm.Input == null ? $"'{alarm.Element.Name}' names no value (InputNode)."
        : !AmlAddressSpace.IsServedVariable(alarm.Input) ? $"'{alarm.Element.Name}' watches '{alarm.Input.Name}', which is no value."
        : alarm.Kind == AlarmKind.State && alarm.Normal == null ? $"'{alarm.Element.Name}' has no normal value (NormalState)."
        : alarm.Kind != AlarmKind.State && alarm.Limit == null ? $"'{alarm.Element.Name}': its limit is no number."
        : null;

    /// <summary>A name for a new alarm on <paramref name="value"/>, free under its parent.</summary>
    public static string SuggestName(InternalElementType value, AlarmKind kind)
    {
        var stem = value.Name + kind switch { AlarmKind.High => "High", AlarmKind.Low => "Low", _ => "Alarm" };
        var taken = Siblings(value).Select(e => e.Name).ToHashSet(StringComparer.Ordinal);
        var name = stem;
        for (var i = 2; taken.Contains(name); i++) name = stem + i.ToString(CultureInfo.InvariantCulture);
        return name;
    }

    /// <summary>
    /// Creates an alarm on <paramref name="value"/> beside it, under its parent:
    /// below the value itself, the value would become an object.
    /// </summary>
    /// <exception cref="AlarmException">The request does not fit the value, or the document lacks the base library.</exception>
    public static InternalElementType Create(InternalElementType value, AlarmKind kind, double? limit, bool? normal, ushort severity, string message, string name)
    {
        var doc = value.CAEXDocument ?? throw new AlarmException(AlarmProblem.NotAValue, $"'{value.Name}' is not part of a document.");
        if (!AmlAddressSpace.IsServedVariable(value))
            throw new AlarmException(AlarmProblem.NotAValue, $"'{value.Name}' is no value: an alarm watches an element with a Value.");
        var dataType = value.Attribute["Value"]?.AttributeDataType;
        if (kind == AlarmKind.State && dataType != "xs:boolean")
            throw new AlarmException(AlarmProblem.NotBoolean, $"'{value.Name}' is no boolean; a state alarm watches true or false.");
        if (kind != AlarmKind.State && (dataType == null || !Numbers.Contains(dataType)))
            throw new AlarmException(AlarmProblem.NotANumber, $"'{value.Name}' is no number; a limit alarm watches a number.");
        if (kind != AlarmKind.State && (limit == null || double.IsNaN(limit.Value) || double.IsInfinity(limit.Value)))
            throw new AlarmException(AlarmProblem.NotANumber, "The limit is no number.");
        if (severity is < 1 or > 1000)
            throw new AlarmException(AlarmProblem.BadSeverity, $"A severity is 1 to 1000, not {severity}.");
        if (string.IsNullOrWhiteSpace(name) || Siblings(value).Any(e => e.Name == name))
            throw new AlarmException(AlarmProblem.NameTaken, $"'{name}' is taken beside '{value.Name}'.");
        var type = Class(doc, kind == AlarmKind.State ? OffNormal : ExclusiveLevel)
            ?? throw new AlarmException(AlarmProblem.NoBaseLibrary, "The document has no OPC UA base library; import a NodeSet first, which brings it.");

        var optional = kind switch { AlarmKind.High => "HighLimit", AlarmKind.Low => "LowLimit", _ => null };
        var instance = TypeInstantiator.Instantiate(type, name, new InstantiationOptions { IncludeOptional = p => p == optional }).Instance;
        if (optional != null) Set(instance.InternalElement[optional], limit!.Value.ToString("R", CultureInfo.InvariantCulture));
        Set(instance.InternalElement["Severity"], severity.ToString(CultureInfo.InvariantCulture));
        Set(instance.InternalElement["Message"], message);
        Set(instance.InternalElement["ConditionName"], name);

        Vdi3682Links.EnsureLibraries(doc.CAEXFile);
        var parent = (IInternalElementContainer)value.CAEXParent!;
        parent.InternalElement.Insert(instance, asFirst: false);
        var alarm = parent.InternalElement.Last();
        Name(alarm.InternalElement["InputNode"], value);
        if (kind == AlarmKind.State && alarm.InternalElement["NormalState"] is { } normalState)
            Name(normalState, AddNormalValue(alarm, normalState, normal == true));
        return alarm;
    }

    /// <summary>
    /// The property NormalValue that NormalState names: a copy of NormalState
    /// (a property with its link to the alarm, Annex A) holding a boolean.
    /// </summary>
    private static InternalElementType AddNormalValue(InternalElementType alarm, InternalElementType like, bool normal)
    {
        var copy = (InternalElementType)like.Copy(deepCopy: true, assignNewIDs: true);
        copy.Name = "NormalValue";
        foreach (var name in new[] { "Value", "BrowseName", "NodeId", RefNode })
            if (copy.Attribute[name] is { } old) copy.Attribute.RemoveElement(old);
        var value = copy.Attribute.Append("Value");
        value.AttributeDataType = "xs:boolean";
        value.RefAttributeType = "[ATL_http://opcfoundation.org/UA/]/[Boolean]";
        value.Value = normal ? "true" : "false";
        alarm.InternalElement.Insert(copy, asFirst: false);
        var added = alarm.InternalElement["NormalValue"]!;
        // The property hangs off the alarm as NormalState does: by the same interface of the alarm.
        var likeEnds = like.ExternalInterface.Select(ei => ei.ID).ToHashSet(StringComparer.Ordinal);
        foreach (var link in alarm.InternalLink.Where(l => likeEnds.Contains(l.RefPartnerSideA) || likeEnds.Contains(l.RefPartnerSideB)).ToList())
        {
            var mine = likeEnds.Contains(link.RefPartnerSideA) ? link.RefPartnerSideA : link.RefPartnerSideB;
            var other = mine == link.RefPartnerSideA ? link.RefPartnerSideB : link.RefPartnerSideA;
            var index = like.ExternalInterface.ToList().FindIndex(ei => ei.ID == mine);
            var end = added.ExternalInterface.ElementAtOrDefault(index);
            if (end == null) continue;
            var copied = alarm.New_InternalLink(added.Name);
            copied.RefPartnerSideA = link.RefPartnerSideA == mine ? end.ID : other;
            copied.RefPartnerSideB = link.RefPartnerSideB == mine ? end.ID : other;
        }
        return added;
    }

    /// <summary>Makes a NodeId property name <paramref name="target"/>: by ID, and by its NodeId where it has one.</summary>
    private static void Name(InternalElementType? property, InternalElementType target)
    {
        if (property == null) return;
        if (string.IsNullOrEmpty(target.ID)) throw new AlarmException(AlarmProblem.NotAValue, $"'{target.Name}' has no ID to reference.");
        if (property.Attribute[RefNode] is { } old) property.Attribute.RemoveElement(old);
        var reference = property.Attribute.Append(RefNode);
        reference.RefAttributeType = Vdi3682Links.OwnLib + "/" + RefNode;
        reference.AttributeDataType = "xs:IDREF";
        reference.Value = target.ID;
        if (OwnNodeId(target) is { } address && property.Attribute["Value"] is { } value) AnnexANodeId.Fill(value, address);
    }

    /// <summary>The element a NodeId property names: by its refNode, else by the NodeId in its Value.</summary>
    internal static InternalElementType? Referenced(InternalElementType? property)
    {
        if (property == null || property.CAEXDocument is not { } doc) return null;
        if (property.Attribute[RefNode]?.Value is { Length: > 0 } id)
            return doc.FindByID(id, true, null) as InternalElementType;
        if (property.Attribute["Value"] is not { } value) return null;
        UaNodeAddress address;
        try { address = AnnexANodeId.Read(value); }
        catch (AddressingException) { return null; }
        return doc.CAEXFile.InstanceHierarchy.SelectMany(ih => ih.Descendants<InternalElementType>())
            .FirstOrDefault(e => OwnNodeId(e) is { } own && own with { ServerUri = null } == address with { ServerUri = null });
    }

    /// <summary>The NodeId an element carries: its NodeId attribute, else the one Annex A puts into its ID.</summary>
    internal static UaNodeAddress? OwnNodeId(InternalElementType e)
    {
        try
        {
            if (AnnexANodeId.Of(e) is { } attribute) return attribute;
        }
        catch (AddressingException) { /* an indirect NodeId; the ID may still carry one */ }
        var decoded = WebUtility.UrlDecode(e.ID ?? "");
        return decoded.StartsWith("nsu=", StringComparison.Ordinal) && UaNodeAddress.TryParse(decoded, null, out var fromId) ? fromId : null;
    }

    private static IEnumerable<InternalElementType> Siblings(InternalElementType value) =>
        value.CAEXParent is IInternalElementContainer parent ? parent.InternalElement : [];

    private static void Set(InternalElementType? property, string value)
    {
        if (property?.Attribute["Value"] is { } attribute) attribute.Value = value;
    }

    private static string? ValueOf(InternalElementType? property) =>
        property?.Attribute["Value"]?.Value is { Length: > 0 } v ? v : null;

    private static double? Number(string? text) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var n) && !double.IsNaN(n) && !double.IsInfinity(n) ? n : null;

    private static IEnumerable<SystemUnitFamilyType> ChainOf(InternalElementType ie) =>
        UaTypes.TypeOf(ie) is { } type ? UaTypes.Chain(type) : [];

    private static bool IsUa(SystemUnitFamilyType type, string name) =>
        type.Name == name && type.CAEXParent is SystemUnitClassLibType { Name: UaLibrary };

    private static SystemUnitFamilyType? Class(CAEXDocument doc, string name) =>
        UaTypes.Resolve(doc, $"[{UaLibrary}]/[{name}]");
}
