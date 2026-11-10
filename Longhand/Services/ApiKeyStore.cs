using Windows.Security.Credentials;

namespace Longhand.Services;

/// <summary>Keeps the Anthropic API key in Windows Credential Manager (PasswordVault), never in files or code.</summary>
public static class ApiKeyStore
{
    const string Resource = "Longhand";
    const string User = "anthropic-api-key";

    public static string? Load()
    {
        try
        {
            var credential = new PasswordVault().Retrieve(Resource, User);
            credential.RetrievePassword();
            return string.IsNullOrWhiteSpace(credential.Password) ? null : credential.Password;
        }
        catch
        {
            return null; // Retrieve throws when nothing is stored.
        }
    }

    public static void Save(string key)
    {
        Clear();
        new PasswordVault().Add(new PasswordCredential(Resource, User, key));
    }

    public static void Clear()
    {
        try
        {
            var vault = new PasswordVault();
            vault.Remove(vault.Retrieve(Resource, User));
        }
        catch
        {
            // Retrieve throws when nothing is stored.
        }
    }
}
