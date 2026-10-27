using System;
using System.IO;
using BepInEx;

namespace OldenPerf;

// The native depth adapter relies on shader bytecode stored in resources.assets. The texture patch rewrites that
// file, so a whole-file hash alone would disable them. Accepted hashes: the shipped file, and the texture-patched file whose
// 147 Shader objects were proven byte-identical to the shipped ones (only Texture2D and Sprite objects differ;
// RunScratch/olden-core-20261027/cmp_assets.py), and the same file with only AudioClip load types changed
// (RunScratch/olden-audio-20261027/verify.py). Any other file fails closed.
static class ShaderAssetGuard
{
    static readonly string[] Accepted =
    {
        "f286cdd9ade61c0dc2fbdb95c0faaac73b6b1d016b0b000921885ef486e07b9f", // shipped build 26134266
        "0a7130bedefea69049630ba8cff3f2a62c064054f6b507047a26a762b375bf79", // 27 Oct texture patch, shaders identical
        "dd371883359db73b9ddf9effbf63e1020dad1151b60221e682de8b8a6dd08a77", // 27 Oct texture patch + AudioClip m_LoadType 0->1 only (3,523 bytes), shaders identical
    };
    static bool? _verified;

    public static bool Verified => _verified ??= Check();

    static bool Check()
    {
        try
        {
            string hash = FileHash.Sha256(Path.Combine(Paths.GameRootPath, "HeroesOldenEra_Data", "resources.assets"));
            bool ok = Array.IndexOf(Accepted, hash) >= 0;
            if (!ok) Plugin.I.Log.LogWarning("shader asset unverified: resources.assets " + hash);
            return ok;
        }
        catch (Exception e) { Plugin.I.Log.LogWarning("shader asset unreadable: " + e.Message); return false; }
    }
}
