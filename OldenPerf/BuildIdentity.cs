using System;
using System.IO;
using BepInEx;

namespace OldenPerf;

static class BuildIdentity
{
    const string Verified = "eb57aeecaa27ff5764ddff76c5a3459857ed73f3abdefb14cfa370facb85e940";
    static string _fingerprint;
    public static string Fingerprint
    {
        get => _fingerprint ??= FileHash.Sha256(Path.Combine(Paths.GameRootPath, "GameAssembly.dll"));
    }
    public static bool IsVerified => Fingerprint == Verified;
}
