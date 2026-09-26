// Removes where the servers of a document can be reached, before the document
// is passed on: the endpoint and URI of a mirrored server, the
// ServerInstanceUri of every NodeId below it (which usually carries the host
// name), and EndpointURL and DiscoveryURL of a server element after BPR 007 and
// DIN SPEC 16592. What a node is known by (NamespaceUri, identifier) and how a
// server is secured stay, and so do the libraries, whose NodeId type declares
// ServerInstanceUri without a value.
//
// Host names can hide elsewhere too, in a mirrored value or a description; the
// report names every place in the instance hierarchies where one still
// appears, so the user can decide about it.

using System.Xml.Linq;

namespace OpcUaAml.Server;

public sealed record StripReport(int Removed, IReadOnlyList<string> Hosts, IReadOnlyList<string> Remaining)
{
    public override string ToString() =>
        $"{Removed} address attribute(s) removed" +
        (Hosts.Count > 0 ? $"; hosts: {string.Join(", ", Hosts)}" : "") +
        (Remaining.Count > 0 ? $"; a host name still appears in {Remaining.Count} place(s)" : "");
}

public static class ServerAddresses
{
    /// <summary>The attributes that say where a server is reached.</summary>
    public static readonly IReadOnlySet<string> AddressAttributes = new HashSet<string>(StringComparer.Ordinal)
    {
        "ServerUri", "EndpointUrl", "ServerInstanceUri", "EndpointURL", "DiscoveryURL",
    };

    private static readonly HashSet<string> NotIdentifying = new(StringComparer.OrdinalIgnoreCase)
    {
        "localhost", "127.0.0.1", "::1", "[::1]",
    };

    /// <summary>Removes the address attributes from the instance hierarchies of <paramref name="document"/>, in place.</summary>
    public static StripReport Strip(XDocument document)
    {
        var hierarchies = document.Root?.Elements().Where(e => e.Name.LocalName == "InstanceHierarchy").ToList() ?? new();
        var addresses = hierarchies.SelectMany(h => h.Descendants())
            .Where(e => e.Name.LocalName == "Attribute" && AddressAttributes.Contains((string?)e.Attribute("Name") ?? ""))
            .ToList();

        var hosts = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var attribute in addresses)
            if (HostOf(ValueOf(attribute)) is { } host && !NotIdentifying.Contains(host)) hosts.Add(host);
        foreach (var attribute in addresses) attribute.Remove();

        var remaining = hosts.Count == 0 ? new List<string>() : hierarchies.SelectMany(h => h.DescendantsAndSelf())
            .SelectMany(e => Mentions(e, hosts))
            .ToList();
        return new StripReport(addresses.Count, hosts.ToList(), remaining);
    }

    /// <summary>The host of a URL (opc.tcp://host:4840/…) or of a URN (urn:host:…).</summary>
    internal static string? HostOf(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        if (Uri.TryCreate(value, UriKind.Absolute, out var uri) && !string.IsNullOrEmpty(uri.Host)) return uri.Host.ToLowerInvariant();
        var parts = value.Split(':');
        return parts.Length >= 3 && parts[0].Equals("urn", StringComparison.OrdinalIgnoreCase) && parts[1].Length > 0
            ? parts[1].ToLowerInvariant()
            : null;
    }

    private static string? ValueOf(XElement attribute) =>
        attribute.Elements().FirstOrDefault(e => e.Name.LocalName == "Value")?.Value;

    /// <summary>Where an element names one of the hosts: its name, the value of one of its own attributes, its description.</summary>
    private static IEnumerable<string> Mentions(XElement element, IReadOnlyCollection<string> hosts)
    {
        bool Names(string? text) => text != null && hosts.Any(h => text.Contains(h, StringComparison.OrdinalIgnoreCase));
        var kind = element.Name.LocalName;
        if (kind is "InternalElement" or "InstanceHierarchy" && Names((string?)element.Attribute("Name")))
            yield return $"{PathOf(element)}: name";
        if (kind == "Description" && Names(element.Value))
            yield return $"{PathOf(element.Parent!)}: description";
        if (kind == "Attribute" && element.Elements().Any(e => e.Name.LocalName == "Value" && Names(e.Value)))
            yield return $"{PathOf(element)}: value";
    }

    /// <summary>"IH/Element/Sub: attribute A/B" as the editor's tree would lead to it.</summary>
    private static string PathOf(XElement element)
    {
        var elements = new List<string>();
        var attributes = new List<string>();
        for (var e = element; e != null && e.Name.LocalName != "CAEXFile"; e = e.Parent)
        {
            var name = (string?)e.Attribute("Name") ?? "";
            if (e.Name.LocalName == "Attribute") attributes.Insert(0, name);
            else if (e.Name.LocalName is "InternalElement" or "InstanceHierarchy") elements.Insert(0, name);
        }
        var path = string.Join("/", elements);
        return attributes.Count > 0 ? $"{path}, attribute {string.Join("/", attributes)}" : path;
    }
}
