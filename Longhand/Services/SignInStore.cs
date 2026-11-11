using System;
using System.Collections.Generic;
using Windows.Security.Credentials;

namespace Longhand.Services;

/// <summary>
/// Keeps the ChatGPT sign-in in Windows Credential Manager (PasswordVault), never in files or code. Its tokens are longer than
/// one vault entry holds, so it is split over numbered entries.
/// </summary>
public static class SignInStore
{
    const string Resource = "Longhand";
    const string Prefix = "chatgpt-signin-";
    const int Chunk = 1000;
    static readonly object Gate = new();

    public static CodexSignIn? Load()
    {
        lock (Gate)
        {
            var parts = new List<string>();
            var vault = new PasswordVault();
            for (int i = 0; ; i++)
            {
                try
                {
                    var credential = vault.Retrieve(Resource, Prefix + i);
                    credential.RetrievePassword();
                    parts.Add(credential.Password);
                }
                catch
                {
                    break; // Retrieve throws past the last part.
                }
            }
            return parts.Count == 0 ? null : CodexSignIn.Parse(string.Concat(parts));
        }
    }

    public static void Save(CodexSignIn signIn)
    {
        lock (Gate)
        {
            ClearLocked();
            var json = signIn.ToJson();
            var vault = new PasswordVault();
            for (int i = 0, n = 0; i < json.Length; i += Chunk, n++)
                vault.Add(new PasswordCredential(Resource, Prefix + n, json.Substring(i, Math.Min(Chunk, json.Length - i))));
        }
    }

    public static void Clear()
    {
        lock (Gate) ClearLocked();
    }

    static void ClearLocked()
    {
        var vault = new PasswordVault();
        for (int i = 0; ; i++)
        {
            try { vault.Remove(vault.Retrieve(Resource, Prefix + i)); }
            catch { return; } // Retrieve throws past the last part.
        }
    }
}
