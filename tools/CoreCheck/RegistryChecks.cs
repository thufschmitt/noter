using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Longhand.Services;

/// <summary>Checks for finding MCP servers in the registry and telling how each one lets you in.</summary>
static class RegistryChecks
{
    public static async Task RunAsync(Action<string, bool> check)
    {
        await using (var registry = new FakeRegistry())
        {
            var client = new McpRegistry(baseUrl: registry.BaseUrl);
            var page = await client.SearchAsync("todo", null, default);
            var request = registry.Requests.Last();
            check("the search asks for the latest version of each server", request.Query.Contains("version=latest") && request.Query.Contains("search=todo"));

            var names = page.Servers.Select(s => s.Name).ToList();
            check("servers hosted online are listed once", names.SequenceEqual(new[] { "io.github.acme/todo-list", "com.example/tenant", "com.example/keyed", "com.example/optional-key" }));
            check("others are counted as hidden (local, private, plain http, custom header)", page.Hidden == 4);
            check("a deleted server is not offered at all", !names.Contains("com.example/old"));
            check("the title and site come through", page.Servers[0].Title == "Todo List" && page.Servers[0].Website == "https://acme.example" && page.Servers[0].Host == "mcp.acme.example");
            check("the next page is available", page.NextCursor == "next-1");

            var tenant = page.Servers[1].Remote;
            check("blanks in the address are found", tenant.Variables.Count == 1 && tenant.Variables[0].Name == "tenant" && tenant.Variables[0].Choices.SequenceEqual(new[] { "eu", "us" }) && tenant.Variables[0].Default == "eu");
            check("blanks are filled in, with the default when nothing is typed", tenant.Resolve(new Dictionary<string, string> { ["tenant"] = "us" }) == "https://us.example.com/mcp" && tenant.Resolve(new Dictionary<string, string>()) == "https://eu.example.com/mcp");
            check("a blank that makes the address unusable is refused", tenant.Resolve(new Dictionary<string, string> { ["tenant"] = "localhost/x#" }) is null);
            check("a bearer token is flagged as needed, but not when optional", page.Servers[2].Remote.TokenRequired && !page.Servers[3].Remote.TokenRequired && !page.Servers[0].Remote.TokenRequired);
            check("a header's own variables are not part of the address", page.Servers[2].Remote.Variables.Count == 0);

            var second = await client.SearchAsync("todo", "next-1", default);
            check("the next page is read, and SSE servers are accepted", second.Servers.Count == 1 && second.Servers[0].Name == "com.example/second" && second.NextCursor is null);
        }

        check("names for the API are tidy", McpRegistry.Slug("io.github.Pipeworx-io/To_Do List!") == "to-do-list" && McpRegistry.Slug("weird/") == "server" && McpRegistry.Slug("ac.inference.sh/mcp") == "mcp");
        check("addresses on this PC or a private network are refused", !McpRegistry.IsPublicHttps("https://localhost/mcp") && !McpRegistry.IsPublicHttps("https://10.0.0.4/mcp") && !McpRegistry.IsPublicHttps("https://172.20.1.1/mcp") && !McpRegistry.IsPublicHttps("http://example.com/mcp") && McpRegistry.IsPublicHttps("https://172.32.1.1/mcp") && McpRegistry.IsPublicHttps("https://mcp.example.com/x"));

        // ----- how each server lets you in -----
        await using var fake = new FakeOAuthServer();
        var oauth = new McpOAuth();
        check("a server that asks for sign-in is recognised", await oauth.InspectAsync(fake.BaseUrl + "/mcp", default) == McpAccess.SignIn);
        check("a server that asks for nothing is open", await oauth.InspectAsync(fake.BaseUrl + "/open", default) == McpAccess.Open);
        check("a server that wants a token but has no sign-in needs one pasted", await oauth.InspectAsync(fake.BaseUrl + "/closed", default) == McpAccess.Token);
        check("a protocol quirk is not mistaken for a sign-in problem", await oauth.InspectAsync(fake.BaseUrl + "/406", default) == McpAccess.Open);
        var broken = false;
        try { await oauth.InspectAsync(fake.BaseUrl + "/nowhere", default); } catch (InvalidOperationException) { broken = true; }
        check("a server that isn't there is reported", broken);
    }
}
