// Finding servers instead of typing their address (OPC 10000-12): a discovery
// server, the Local Discovery Server at port 4840 of a computer or any server
// about itself, lists the servers it knows (FindServers), and each server
// lists the endpoints it offers (GetEndpoints) with their security and the
// kinds of user it admits.

using Opc.Ua;

namespace OpcUaAml.Server;

/// <summary>A server a discovery server knows.</summary>
public sealed record DiscoveredServer(string Name, string ApplicationUri, string Kind, IReadOnlyList<string> DiscoveryUrls);

/// <summary>An endpoint of a server: where, how secured, and which users it admits (Anonymous, UserName, Certificate …).</summary>
public sealed record DiscoveredEndpoint(string Url, string SecurityMode, string SecurityPolicy, IReadOnlyList<string> UserTokens);

public static class UaDiscovery
{
    /// <summary>The port a Local Discovery Server is registered for.</summary>
    public const int DiscoveryPort = 4840;

    /// <summary>
    /// The discovery URL for what a user typed: a host, a host and port, or a
    /// whole URL; without a port the discovery port, without anything this computer.
    /// </summary>
    public static string DiscoveryUrl(string text)
    {
        var t = text.Trim();
        if (t.Length == 0) t = "localhost";
        if (!t.Contains("://", StringComparison.Ordinal)) t = "opc.tcp://" + t;
        if (!Uri.TryCreate(t, UriKind.Absolute, out var uri)) return t;
        return uri.IsDefaultPort || uri.Port < 0
            ? $"{uri.Scheme}://{uri.Host}:{DiscoveryPort}{uri.PathAndQuery.TrimEnd('/')}"
            : t;
    }

    /// <summary>The servers the discovery server at <paramref name="where"/> knows.</summary>
    public static async Task<IReadOnlyList<DiscoveredServer>> FindServersAsync(string where, string? pkiRoot = null, CancellationToken ct = default)
    {
        var url = DiscoveryUrl(where);
        using var client = await CreateAsync(url, pkiRoot, ct).ConfigureAwait(false);
        try
        {
            var servers = await client.FindServersAsync(null, ct).ConfigureAwait(false);
            return servers.Select(s => new DiscoveredServer(
                s.ApplicationName?.Text is { Length: > 0 } name ? name : s.ApplicationUri,
                s.ApplicationUri, s.ApplicationType.ToString(),
                (IReadOnlyList<string>?)s.DiscoveryUrls?.ToList() ?? [])).ToList();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new UaConnectionException($"The discovery server at '{url}' did not answer: {ex.Message}", ex);
        }
    }

    /// <summary>The endpoints the server at <paramref name="url"/> offers.</summary>
    public static async Task<IReadOnlyList<DiscoveredEndpoint>> GetEndpointsAsync(string url, string? pkiRoot = null, CancellationToken ct = default)
    {
        using var client = await CreateAsync(url, pkiRoot, ct).ConfigureAwait(false);
        try
        {
            var endpoints = await client.GetEndpointsAsync(null, ct).ConfigureAwait(false);
            return endpoints.Select(e => new DiscoveredEndpoint(
                e.EndpointUrl, e.SecurityMode.ToString(), SecurityPolicies.GetDisplayName(e.SecurityPolicyUri) ?? e.SecurityPolicyUri ?? "",
                (IReadOnlyList<string>?)e.UserIdentityTokens?.Select(t => t.TokenType.ToString()).Distinct().ToList() ?? [])).ToList();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new UaConnectionException($"'{url}' did not list its endpoints: {ex.Message}", ex);
        }
    }

    private static async Task<DiscoveryClient> CreateAsync(string url, string? pkiRoot, CancellationToken ct)
    {
        OpcUaStack.UseOwnTypes();
        var options = new UaConnectOptions { EndpointUrl = url };
        if (pkiRoot != null) options = new UaConnectOptions { EndpointUrl = url, PkiRoot = pkiRoot };
        var config = await UaClient.CreateConfigurationAsync(options, ct).ConfigureAwait(false);
        var endpoint = EndpointConfiguration.Create(config);
        endpoint.OperationTimeout = 10000;
        try
        {
            return await DiscoveryClient.CreateAsync(config, new Uri(url), endpoint, DiagnosticsMasks.None, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new UaConnectionException($"No discovery server at '{url}': {ex.Message}", ex);
        }
    }
}
