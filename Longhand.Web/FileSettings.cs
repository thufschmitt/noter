using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using Longhand.Services;

namespace Longhand.Web;

/// <summary>Writes a file so a crash never leaves half of it, and, for secrets, so it is never readable by anyone else (not even for an instant).</summary>
public static class PrivateFile
{
    public static void Write(string path, string text, bool priv)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var temp = path + ".tmp";
        var options = new FileStreamOptions { Mode = FileMode.Create, Access = FileAccess.Write };
        if (priv && !OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        using (var stream = new FileStream(temp, options)) stream.Write(Encoding.UTF8.GetBytes(text));
        File.Move(temp, path, overwrite: true);
    }
}

/// <summary>Named values in one JSON file. Every call reads the file afresh, so several tabs (and the server's own tasks) agree.</summary>
public sealed class FileSettings : ISettings
{
    readonly string _path;
    readonly bool _private;
    readonly object _lock = new();

    public FileSettings(string path, bool isPrivate = false)
    {
        _path = path;
        _private = isPrivate;
    }

    Dictionary<string, string> Read()
    {
        try
        {
            if (File.Exists(_path) && JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(_path)) is { } d) return d;
        }
        catch (Exception ex) when (ex is IOException or JsonException) { }
        return new();
    }

    void Write(Dictionary<string, string> values) => PrivateFile.Write(_path, JsonSerializer.Serialize(values), _private);

    public IReadOnlyList<KeyValuePair<string, string>> All()
    {
        lock (_lock) return Read().ToList();
    }

    public string? Get(string key)
    {
        lock (_lock) return Read().TryGetValue(key, out var v) ? v : null;
    }

    public void Set(string key, string value)
    {
        lock (_lock)
        {
            var d = Read();
            d[key] = value;
            Write(d);
        }
    }

    public void Remove(string key)
    {
        lock (_lock)
        {
            var d = Read();
            if (d.Remove(key)) Write(d);
        }
    }
}

/// <summary>Secrets in a file only the user running the server can read.</summary>
public sealed class FileSecrets : ISecrets
{
    readonly FileSettings _file;

    public FileSecrets(string path) => _file = new FileSettings(path, isPrivate: true);

    public string? Get(string name) => _file.Get(name);
    public void Set(string name, string secret) => _file.Set(name, secret);
    public void Remove(string name) => _file.Remove(name);
}
