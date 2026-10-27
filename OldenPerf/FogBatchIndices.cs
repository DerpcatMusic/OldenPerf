using System;
using System.Collections.Generic;

namespace OldenPerf;

// The batch's shader vertex IDs and their order must survive compaction verbatim. We remove complete
// source-model index ranges; we never renumber vertices or move the engine's vertex/instance buffers.
static class FogBatchIndices
{
    public readonly record struct Part(int Start, int Count, float MinX, float MaxX, float MinZ, float MaxZ, bool Cullable);
    public readonly record struct Footprint(float MinX, float MaxX, float MinZ, float MaxZ, bool Cullable);

    // Hex/Environment's actual batched vertex shader gates flag motion on bit 8 and tree sway on
    // bit 16. Nonzero default power parameters do not imply deformation. Flag displacement is
    // power * sin(phase) * vertexColor.r (Color32.r / 255). Tree sway is bounded by
    // (0.8 * relativeWorldY)^2 * (cos(phase)+1) * (-0.02, 0, -0.05).
    public static bool TryDeformationPadding(byte flags, float powerX, float powerY, float powerZ,
        float relativeWorldHeight, out float x, out float z)
    {
        x = z = 0;
        double y = 0;
        if ((flags & 8) != 0)
        {
            if (!float.IsFinite(powerX) || !float.IsFinite(powerY) || !float.IsFinite(powerZ)) return false;
            x = Math.Abs(powerX); y = Math.Abs(powerY); z = Math.Abs(powerZ);
        }
        if ((flags & 16) != 0)
        {
            if (!float.IsFinite(relativeWorldHeight)) return false;
            double height = Math.Abs(relativeWorldHeight) + y;
            // Round outward beyond the shader float constants and intermediate rounding.
            x = (float)(x + 0.025601 * height * height);
            z = (float)(z + 0.064001 * height * height);
        }
        return float.IsFinite(x) && float.IsFinite(z);
    }

    // The engine writes an instance ID for every generated vertex. Derive ranges from that actual
    // shader input instead of assuming that a patched ref cursor observes a particular native call order.
    public static bool TryMap(ushort[] indices, ushort[] vertexInstances,
        IReadOnlyDictionary<ushort, Footprint> models, out Part[] parts)
    {
        parts = Array.Empty<Part>();
        if (indices == null || vertexInstances == null || models == null || indices.Length % 3 != 0) return false;
        var result = new List<Part>();
        int start = 0, previous = -2;
        void Finish(int end)
        {
            if (end == start) return;
            Footprint f = default;
            if (previous >= 0) models.TryGetValue((ushort)previous, out f);
            result.Add(new(start, end - start, f.MinX, f.MaxX, f.MinZ, f.MaxZ, f.Cullable));
        }
        for (int i = 0; i < indices.Length; i += 3)
        {
            int a = indices[i], b = indices[i + 1], c = indices[i + 2];
            if (a >= vertexInstances.Length || b >= vertexInstances.Length || c >= vertexInstances.Length) return false;
            int id = vertexInstances[a];
            if (id != vertexInstances[b] || id != vertexInstances[c]) id = -1; // mixed/unknown geometry stays drawn
            if (id == previous) continue;
            Finish(i); start = i; previous = id;
        }
        Finish(indices.Length);
        parts = result.ToArray();
        return true;
    }

    public static bool Valid(ushort[] indices, Part[] parts, int vertices)
    {
        if (indices == null || parts == null || vertices <= 0 || indices.Length % 3 != 0) return false;
        long end = 0;
        foreach (var p in parts)
        {
            if (p.Start != end || p.Count < 0 || p.Count % 3 != 0) return false;
            end += p.Count;
            if (end > indices.Length) return false;
        }
        if (end != indices.Length) return false;
        foreach (ushort i in indices) if (i >= vertices) return false;
        return true;
    }

    // Requires a validated stream. Invalid/edge-crossing/deforming footprints are retained.
    public static int Compact(ushort[] original, Part[] parts, int[] sat, int width, int height,
        ushort[] output, bool[] hidden)
    {
        Classify(parts, sat, width, height, hidden);
        return CopyVisible(original, parts, output, hidden);
    }

    public static void Classify(Part[] parts, int[] sat, int width, int height, bool[] hidden)
    {
        for (int i = 0; i < parts.Length; i++)
        {
            var p = parts[i];
            hidden[i] = p.Cullable &&
                SectionCullState.TileRange(p.MinX, p.MaxX, width, 1, out int x0, out int x1) &&
                SectionCullState.TileRange(p.MinZ, p.MaxZ, height, 1, out int z0, out int z1) &&
                !SectionCullState.RectHasReveal(sat, width, x0, z0, x1, z1);
        }
    }

    public static int CopyVisible(ushort[] original, Part[] parts, ushort[] output, bool[] hidden)
    {
        int cursor = 0;
        for (int i = 0; i < parts.Length; i++)
        {
            var p = parts[i];
            if (hidden[i]) continue;
            Array.Copy(original, p.Start, output, cursor, p.Count);
            cursor += p.Count;
        }
        return cursor;
    }
}
