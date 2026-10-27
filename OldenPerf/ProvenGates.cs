using System;
using System.Collections.Generic;
using System.IO;
using BepInEx;
using UnityEngine;

namespace OldenPerf;

// Equality proofs that passed, remembered across launches. A proof holds for one game build, GPU driver and resolution;
// any change gives a different key and the proof runs again. Delete oldenperf/proven-gates.txt to force re-validation.
static class ProvenGates
{
    static readonly string FilePath = Path.Combine(Paths.BepInExRootPath, "oldenperf", "proven-gates.txt");
    static HashSet<string> _keys;

    public static string Key(string gate, int width, int height) =>
        $"{gate}|{BuildIdentity.Fingerprint}|{SystemInfo.graphicsDeviceName}|{SystemInfo.graphicsDeviceVersion}|{width}x{height}";

    public static bool Has(string key)
    {
        if (_keys == null)
        {
            _keys = new HashSet<string>();
            try { if (File.Exists(FilePath)) _keys.UnionWith(File.ReadAllLines(FilePath)); } catch (Exception) { }
        }
        return _keys.Contains(key);
    }

    public static void Add(string key)
    {
        if (Has(key)) return;
        _keys.Add(key);
        try { File.AppendAllText(FilePath, key + "\n"); } catch (Exception) { }
    }
}
