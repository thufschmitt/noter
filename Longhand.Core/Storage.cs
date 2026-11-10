using System.Collections.Generic;

namespace Longhand.Services;

/// <summary>Small named values that aren't secret: Windows local settings in the app, a JSON file on the server.</summary>
public interface ISettings
{
    IReadOnlyList<KeyValuePair<string, string>> All();
    string? Get(string key);
    void Set(string key, string value);
    void Remove(string key);
}

/// <summary>Secrets (tokens, passwords): Windows Credential Manager in the app, a private file on the server.</summary>
public interface ISecrets
{
    string? Get(string name);
    void Set(string name, string secret);
    void Remove(string name);
}
