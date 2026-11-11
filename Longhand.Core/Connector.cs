namespace Longhand.Services;

/// <summary>A connected app: a remote MCP server Longhand calls for the model. <c>Token</c> is sent as its bearer token.</summary>
public sealed record Connector(string Name, string Url, string? Token);
