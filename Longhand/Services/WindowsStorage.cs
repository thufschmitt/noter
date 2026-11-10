using System.Collections.Generic;
using System.Linq;
using Windows.Security.Credentials;
using Windows.Storage;

namespace Longhand.Services;

/// <summary>Settings kept in a Windows local-settings container (or the top level of the local settings).</summary>
public sealed class WindowsSettings : ISettings
{
    readonly ApplicationDataContainer _container;

    public WindowsSettings(ApplicationDataContainer container) => _container = container;

    /// <summary>A named container inside the local settings, created when first used.</summary>
    public static WindowsSettings Container(string name) =>
        new(ApplicationData.Current.LocalSettings.CreateContainer(name, ApplicationDataCreateDisposition.Always));

    public static WindowsSettings Local => new(ApplicationData.Current.LocalSettings);

    public IReadOnlyList<KeyValuePair<string, string>> All() =>
        _container.Values.Where(v => v.Value is string).Select(v => new KeyValuePair<string, string>(v.Key, (string)v.Value)).ToList();

    public string? Get(string key) => _container.Values[key] as string;
    public void Set(string key, string value) => _container.Values[key] = value;
    public void Remove(string key) => _container.Values.Remove(key);
}

/// <summary>Secrets kept in Windows Credential Manager, next to the API key.</summary>
public sealed class WindowsSecrets : ISecrets
{
    const string Resource = "Longhand";

    public string? Get(string name)
    {
        try
        {
            var credential = new PasswordVault().Retrieve(Resource, name);
            credential.RetrievePassword();
            return string.IsNullOrEmpty(credential.Password) ? null : credential.Password;
        }
        catch
        {
            return null; // Retrieve throws when nothing is stored.
        }
    }

    public void Set(string name, string secret)
    {
        Remove(name);
        new PasswordVault().Add(new PasswordCredential(Resource, name, secret));
    }

    public void Remove(string name)
    {
        try
        {
            var vault = new PasswordVault();
            vault.Remove(vault.Retrieve(Resource, name));
        }
        catch
        {
            // Nothing stored.
        }
    }
}
