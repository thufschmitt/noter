namespace Longhand.Services;

/// <summary>A remote MCP server Claude may call, reached by Anthropic's servers: <c>Token</c> is sent as its bearer token.</summary>
public sealed record Connector(string Name, string Url, string? Token);
