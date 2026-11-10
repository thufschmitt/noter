using Windows.Security.Credentials;

namespace Longhand.Services;

/// <summary>Keeps the connector list, which holds bearer tokens, in Windows Credential Manager next to the API key.</summary>
public static class ConnectorStore
{
    const string Resource = "Longhand";
    const string User = "mcp-connectors";

    public static string Load()
    {
        try
        {
            var credential = new PasswordVault().Retrieve(Resource, User);
            credential.RetrievePassword();
            return credential.Password ?? "";
        }
        catch
        {
            return ""; // Retrieve throws when nothing is stored.
        }
    }

    public static void Save(string text)
    {
        var vault = new PasswordVault();
        try { vault.Remove(vault.Retrieve(Resource, User)); } catch { /* nothing stored yet */ }
        if (!string.IsNullOrWhiteSpace(text)) vault.Add(new PasswordCredential(Resource, User, text.Trim()));
    }
}
