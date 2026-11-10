namespace Longhand.Web;

/// <summary>How the server is set up. The command line and environment fill it in; the checks set it directly.</summary>
public sealed class WebOptions
{
    /// <summary>Where conversations, symbols and settings are kept.</summary>
    public string DataDir { get; set; } = "";
    /// <summary>Addresses to listen on. Loopback only by default: there is no sign-in, so don't expose it to a network you don't trust.</summary>
    public string Urls { get; set; } = "http://127.0.0.1:5080";
    /// <summary>Only for checks against a fake of the API.</summary>
    public string? AnthropicBaseUrl { get; set; }
    /// <summary>Overrides ANTHROPIC_API_KEY and the saved key.</summary>
    public string? ApiKey { get; set; }
    /// <summary>Development: serve the page from this folder and reload open tabs when it changes (--www or LONGHAND_WWW).</summary>
    public string? WwwRoot { get; set; }

    public static WebOptions FromArgs(string[] args)
    {
        var o = new WebOptions
        {
            DataDir = System.Environment.GetEnvironmentVariable("LONGHAND_DATA") is { Length: > 0 } d ? d : DefaultDataDir(),
        };
        if (System.Environment.GetEnvironmentVariable("LONGHAND_WWW") is { Length: > 0 } www) o.WwwRoot = www;
        if (System.Environment.GetEnvironmentVariable("LONGHAND_URLS") is { Length: > 0 } urls) o.Urls = urls;
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
