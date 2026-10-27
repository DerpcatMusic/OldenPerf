using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using BepInEx;

namespace OldenPerf;

// SHA-256 of game files, cached by path, length and last-write time. Hashing GameAssembly.dll and resources.assets
// through Wine's bcrypt took about 3.5 s of main-thread time on every launch; an unchanged file now costs one stat.
static class FileHash
{
    static readonly string CachePath = Path.Combine(Paths.BepInExRootPath, "oldenperf", "file-hashes.txt");
    static Dictionary<string, string> _cache;

    public static string Sha256(string path)
    {
        var info = new FileInfo(path);
        string key = $"{info.FullName}|{info.Length}|{info.LastWriteTimeUtc.Ticks}";
        _cache ??= Load();
        if (_cache.TryGetValue(key, out var hash)) return hash;
        using (var s = info.OpenRead()) using (var sha = SHA256.Create()) hash = Convert.ToHexString(sha.ComputeHash(s)).ToLowerInvariant();
        _cache[key] = hash;
        try { File.AppendAllText(CachePath, key + "\t" + hash + "\n"); } catch (Exception) { }
        return hash;
    }

    static Dictionary<string, string> Load()
    {
        var d = new Dictionary<string, string>();
        try
        {
            foreach (var line in File.ReadAllLines(CachePath))
            {
                int tab = line.LastIndexOf('\t');
                if (tab > 0 && line.Length - tab - 1 == 64) d[line[..tab]] = line[(tab + 1)..];
            }
        }
        catch (Exception) { }
        return d;
    }
}
