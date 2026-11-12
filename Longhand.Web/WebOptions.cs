using System;

namespace Longhand.Web;

/// <summary>How the server is set up. The command line and environment fill it in; the checks set it directly.</summary>
public sealed class WebOptions
{
    /// <summary>Where conversations, symbols and settings are kept.</summary>
    public string DataDir { get; set; } = "";
    /// <summary>Addresses to listen on. Loopback only by default: there is no sign-in, so don't expose it to a network you don't trust.</summary>
    public string Urls { get; set; } = "http://127.0.0.1:5080";
    /// <summary>Only for checks against a fake of the Codex backend (LONGHAND_CODEX_URL).</summary>
    public string? CodexBaseUrl { get; set; }
    /// <summary>Only for checks against a fake of the sign-in renewal (LONGHAND_CODEX_REFRESH_URL).</summary>
    public string? CodexRefreshUrl { get; set; }
    /// <summary>A ChatGPT sign-in (the Codex CLI's auth.json) to use until one is pasted into the page; LONGHAND_CODEX_SIGNIN does the same.</summary>
    public string? SignIn { get; set; }
    /// <summary>Development: serve the page from this folder and reload open tabs when it changes (--www or LONGHAND_WWW).</summary>
    public string? WwwRoot { get; set; }
    /// <summary>Only for checks: lets apps on this machine be added (they must be public https otherwise).</summary>
    public bool AllowPrivateApps { get; set; }
    /// <summary>Only for checks against a fake of the MCP registry.</summary>
    public string? RegistryBaseUrl { get; set; }
    /// <summary>Only for checks against a fake of Google's sign-in and Calendar API.</summary>
    public Longhand.Services.GoogleEndpoints? GoogleEndpoints { get; set; }
    /// <summary>How often to ask Nextcloud whether the sign-in was approved; checks make it short.</summary>
    public TimeSpan NextcloudPollInterval { get; set; } = TimeSpan.FromSeconds(2);

    public static WebOptions FromArgs(string[] args)
    {
        var o = new WebOptions
        {
            DataDir = System.Environment.GetEnvironmentVariable("LONGHAND_DATA") is { Length: > 0 } d ? d : DefaultDataDir(),
        };
        // For testing against fakes: another registry, and apps on this machine (normally refused: apps must be public https).
        if (System.Environment.GetEnvironmentVariable("LONGHAND_REGISTRY_URL") is { Length: > 0 } registry) o.RegistryBaseUrl = registry;
        o.AllowPrivateApps = System.Environment.GetEnvironmentVariable("LONGHAND_ALLOW_PRIVATE_APPS") == "1";
        if (System.Environment.GetEnvironmentVariable("LONGHAND_WWW") is { Length: > 0 } www) o.WwwRoot = www;
        if (System.Environment.GetEnvironmentVariable("LONGHAND_URLS") is { Length: > 0 } urls) o.Urls = urls;
        if (System.Environment.GetEnvironmentVariable("LONGHAND_CODEX_URL") is { Length: > 0 } codex) o.CodexBaseUrl = codex;
        if (System.Environment.GetEnvironmentVariable("LONGHAND_CODEX_REFRESH_URL") is { Length: > 0 } refresh) o.CodexRefreshUrl = refresh;
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--data" && i + 1 < args.Length) o.DataDir = args[++i];
            else if (args[i] == "--www" && i + 1 < args.Length) o.WwwRoot = args[++i];
            else if (args[i] == "--urls" && i + 1 < args.Length) o.Urls = args[++i];
        }
        return o;
    }

    static string DefaultDataDir()
    {
        var home = System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData);
        return System.IO.Path.Combine(home, "longhand");
    }
}
