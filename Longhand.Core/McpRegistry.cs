using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Longhand.Services;

/// <summary>A blank in a server's address that the user fills in, such as <c>{instance}</c> in <c>https://{instance}.example.com/mcp</c>.</summary>
public sealed record RegistryVariable(string Name, string Description, string Default, IReadOnlyList<string> Choices, bool Required);

/// <param name="TokenRequired">The registry says it needs an Authorization header, so an access token has to be added.</param>
public sealed record RegistryRemote(string Url, IReadOnlyList<RegistryVariable> Variables, bool TokenRequired)
{
    /// <summary>The address with the blanks filled in, or null when a required blank is empty or the result isn't a usable address.</summary>
    public string? Resolve(IReadOnlyDictionary<string, string> values)
    {
        var url = Url;
        foreach (var v in Variables)
        {
            var value = (values.TryGetValue(v.Name, out var typed) && typed.Trim().Length > 0 ? typed : v.Default).Trim();
            if (value.Length == 0 && v.Required) return null;
            url = url.Replace("{" + v.Name + "}", value);
        }
        return url.Contains('{') || !McpRegistry.IsPublicHttps(url) ? null : url;
    }
}

public sealed record RegistryServer(string Name, string Title, string Description, string? Website, RegistryRemote Remote)
{
    public string Host => Uri.TryCreate(Remote.Url.Replace("{", "").Replace("}", ""), UriKind.Absolute, out var u) ? u.Host : "";
}

/// <param name="Hidden">Servers on the page that can't be used here (nothing hosted online, or a kind of sign-in this app can't do).</param>
public sealed record RegistryPage(IReadOnlyList<RegistryServer> Servers, int Hidden, string? NextCursor);

/// <summary>
/// Searches the official MCP registry (registry.modelcontextprotocol.io). Longhand calls MCP servers over HTTP and can't start
/// programs, so only servers hosted online (a "remote") are of use; ones that are installed and run on your own PC are left out.
/// </summary>
public sealed class McpRegistry
{
    public const string DefaultBaseUrl = "https://registry.modelcontextprotocol.io";
    const int PageSize = 40;

    readonly HttpClient _http;
    readonly string _baseUrl;

    public McpRegistry(HttpClient? http = null, string? baseUrl = null)
    {
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        _baseUrl = (baseUrl ?? DefaultBaseUrl).TrimEnd('/');
    }

    public async Task<RegistryPage> SearchAsync(string? query, string? cursor, CancellationToken ct)
    {
        var url = $"{_baseUrl}/v0/servers?version=latest&limit={PageSize}";
        if (!string.IsNullOrWhiteSpace(query)) url += "&search=" + Uri.EscapeDataString(query.Trim());
        if (!string.IsNullOrEmpty(cursor)) url += "&cursor=" + Uri.EscapeDataString(cursor);

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.UserAgent.ParseAdd("Longhand");
        using var response = await _http.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException($"The registry answered {(int)response.StatusCode}.");
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        return Parse(doc.RootElement);
    }

    public static RegistryPage Parse(JsonElement root)
    {
        var servers = new List<RegistryServer>();
        var seen = new HashSet<string>();
        int hidden = 0;

        if (root.TryGetProperty("servers", out var entries) && entries.ValueKind == JsonValueKind.Array)
        {
            foreach (var entry in entries.EnumerateArray())
            {
                if (!entry.TryGetProperty("server", out var server)) continue;
                var name = Str(server, "name");
                if (name.Length == 0 || !seen.Add(name) || !IsActive(entry)) continue;

                var remote = server.TryGetProperty("remotes", out var remotes) && remotes.ValueKind == JsonValueKind.Array
                    ? remotes.EnumerateArray().Select(ReadRemote).Where(r => r is not null).Select(r => r!)
                        .OrderBy(r => r.Url.Contains('{') ? 1 : 0).FirstOrDefault()
                    : null;
                if (remote is null) { hidden++; continue; }

                var title = Str(server, "title");
                servers.Add(new RegistryServer(name, title.Length > 0 ? title : name, Str(server, "description"), NullIfEmpty(Str(server, "websiteUrl")), remote));
            }
        }

        var next = root.TryGetProperty("metadata", out var meta) ? NullIfEmpty(Str(meta, "nextCursor")) : null;
        return new RegistryPage(servers, hidden, next);
    }

    /// <summary>A remote this app can use, or null: streamable HTTP or SSE, https, reachable from outside, no unsupported header.</summary>
    static RegistryRemote? ReadRemote(JsonElement remote)
    {
        var type = Str(remote, "type");
        if (type is not ("streamable-http" or "sse")) return null;
        var url = Str(remote, "url");
        if (url.Length == 0) return null;

        var variables = new List<RegistryVariable>();
        if (remote.TryGetProperty("variables", out var vars) && vars.ValueKind == JsonValueKind.Object)
            foreach (var v in vars.EnumerateObject()) variables.Add(ReadVariable(v.Name, v.Value));
        // A blank in the address that the registry didn't describe is still a blank to fill in.
        foreach (Match m in Regex.Matches(url, @"\{([^{}]+)\}"))
            if (variables.All(v => v.Name != m.Groups[1].Value)) variables.Add(new RegistryVariable(m.Groups[1].Value, "", "", Array.Empty<string>(), true));
        // Variables used only in a header are the access token's business, not part of the address.
        variables = variables.Where(v => url.Contains("{" + v.Name + "}")).ToList();

        // The address itself must be usable once the blanks are filled in (they could be anything, so check its fixed part).
        if (!IsPublicHttps(Regex.Replace(url, @"\{[^{}]+\}", "x"))) return null;

        bool tokenRequired = false;
        if (remote.TryGetProperty("headers", out var headers) && headers.ValueKind == JsonValueKind.Array)
        {
            foreach (var h in headers.EnumerateArray())
            {
                bool required = h.TryGetProperty("isRequired", out var r) && r.ValueKind == JsonValueKind.True;
                if (!required) continue; // optional headers are skipped, and the server may still work without them
                var name = Str(h, "name");
                var shape = Str(h, "value") + Str(h, "placeholder");
                // Longhand's MCP client only sends "Authorization: Bearer <token>".
                if (name.Equals("Authorization", StringComparison.OrdinalIgnoreCase) && (shape.Length == 0 || shape.TrimStart('"').StartsWith("Bearer", StringComparison.OrdinalIgnoreCase)))
                    tokenRequired = true;
                else
                    return null;
            }
        }
        return new RegistryRemote(url, variables, tokenRequired);
    }

    static RegistryVariable ReadVariable(string name, JsonElement v)
    {
        var choices = v.TryGetProperty("choices", out var c) && c.ValueKind == JsonValueKind.Array
            ? c.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!).ToList()
            : new List<string>();
        return new RegistryVariable(name, Str(v, "description"), Str(v, "default"), choices, v.TryGetProperty("isRequired", out var r) && r.ValueKind == JsonValueKind.True);
    }

    static bool IsActive(JsonElement entry) =>
        !(entry.TryGetProperty("_meta", out var meta) && meta.TryGetProperty("io.modelcontextprotocol.registry/official", out var official)
          && official.TryGetProperty("status", out var status) && status.ValueKind == JsonValueKind.String && status.GetString() != "active");

    /// <summary>
    /// True for an https address on the public internet: the only kind of app address accepted (it was a must when Anthropic's
    /// servers called the apps; Longhand calls them itself now, but keeps the rule).
    /// </summary>
    public static bool IsPublicHttps(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps) return false;
        var host = uri.Host;
        if (host == "localhost" || host.EndsWith(".local", StringComparison.OrdinalIgnoreCase) || host.EndsWith(".internal", StringComparison.OrdinalIgnoreCase)) return false;
        if (IPAddress.TryParse(host, out var ip))
        {
            var b = ip.GetAddressBytes();
            if (IPAddress.IsLoopback(ip) || ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal) return false;
            if (b.Length == 4 && (b[0] == 10 || b[0] == 127 || b[0] == 0 || (b[0] == 172 && b[1] is >= 16 and <= 31) || (b[0] == 192 && b[1] == 168) || (b[0] == 169 && b[1] == 254))) return false;
        }
        return true;
    }

    /// <summary>A name for the server that is safe to send to the API: letters, digits and dashes, from the end of its registry name.</summary>
    public static string Slug(string registryName)
    {
        var last = registryName.Split('/').Last();
        var slug = Regex.Replace(last.ToLowerInvariant(), "[^a-z0-9]+", "-").Trim('-');
        if (slug.Length == 0) slug = "server";
        return slug.Length > 40 ? slug[..40].TrimEnd('-') : slug;
    }

    static string Str(JsonElement e, string name) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    static string? NullIfEmpty(string s) => s.Length == 0 ? null : s;
}
