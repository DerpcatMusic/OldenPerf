using System;

namespace OldenPerf;

// Pure tile and cell bookkeeping for FogCulling and BatchFogCulling (no Unity types).
static class SectionCullState
{
    public const string RootPrefix = "Section Root: ";

    public static bool IsSectionRoot(string name) =>
        name != null && name.StartsWith(RootPrefix, StringComparison.Ordinal);

    // The map is centred on the world origin at one unit per tile: tile t's centre is world t - size/2 on both axes, so
    // tile t covers [t - size/2 - 0.5, t - size/2 + 0.5). Verified live against the game's own cell bounds (bxm+0x40):
    // cell (0,0) is centred at (-72.5, -72.5) and cell (9,9) at (71.5, 71.5) on a 160x160 map.
    // Returns the inclusive tile span of a world span grown by margin tiles, without clamping to the map.
    public static bool TileSpan(float minWorld, float maxWorld, int size, int margin, out int lo, out int hi)
    {
        lo = hi = 0;
        if (size <= 0 || margin < 0) return false;
        if (!float.IsFinite(minWorld) || !float.IsFinite(maxWorld) || minWorld > maxWorld) return false;
        double offset = size / 2.0 + 0.5;
        lo = (int)Math.Floor(minWorld + offset) - margin;
        hi = (int)Math.Floor(maxWorld + offset) + margin;
        return true;
    }

    // As TileSpan, but a span that reaches past the map edge (after the margin) is rejected: it may be visible without any
    // fog grid behind it, so it stays drawn.
    public static bool TileRange(float minWorld, float maxWorld, int size, int margin, out int lo, out int hi) =>
        TileSpan(minWorld, maxWorld, size, margin, out lo, out hi) && lo >= 0 && hi < size;

    // Summed-area table over a revealed-tile grid: sat[(z) * (width + 1) + x] = count of revealed tiles with
    // column < x and row < z. Lets any tile rectangle be queried in four reads.
    public static void BuildSummedArea(bool[] revealed, int width, int height, int[] sat)
    {
        int stride = width + 1;
        Array.Clear(sat, 0, stride);
        for (int z = 0; z < height; z++)
        {
            int row = z * width;
            int acc = 0;
            sat[(z + 1) * stride] = 0;
            for (int x = 0; x < width; x++)
            {
                if (revealed[row + x]) acc++;
                sat[(z + 1) * stride + x + 1] = sat[z * stride + x + 1] + acc;
            }
        }
    }

    // True when any tile in the inclusive rectangle is revealed.
    public static bool RectHasReveal(int[] sat, int width, int x0, int z0, int x1, int z1)
    {
        int stride = width + 1;
        int count = sat[(z1 + 1) * stride + x1 + 1] - sat[z0 * stride + x1 + 1]
            - sat[(z1 + 1) * stride + x0] + sat[z0 * stride + x0];
        return count > 0;
    }

    // bxm groups tiles into 16x16 cells (cell = tile >> 4); "Section Root: sx, sz" is cell sx + sz * cellsX.
    public const int CellTiles = 16;

    public static int CellsAcross(int tiles) => (tiles + CellTiles - 1) / CellTiles;

    public static bool ParseCell(string name, out int sx, out int sz)
    {
        sx = sz = -1;
        if (!IsSectionRoot(name)) return false;
        var parts = name.Substring(RootPrefix.Length).Split(',');
        return parts.Length == 2
            && int.TryParse(parts[0].Trim(), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out sx)
            && int.TryParse(parts[1].Trim(), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out sz);
    }

    // The tiles of a cell grown by one whole cell on every side, clamped to the map. Beyond the map there are no tiles to
    // reveal, so clamping does not hide anything a player could uncover.
    public static void RingTiles(int sx, int sz, int width, int height, out int x0, out int z0, out int x1, out int z1)
    {
        x0 = Math.Max(0, (sx - 1) * CellTiles); z0 = Math.Max(0, (sz - 1) * CellTiles);
        x1 = Math.Min(width - 1, (sx + 2) * CellTiles - 1); z1 = Math.Min(height - 1, (sz + 2) * CellTiles - 1);
    }

    // Deep fog: no revealed tile in the cell or in any of its eight neighbours.
    public static bool CellInDeepFog(int[] sat, int width, int height, int sx, int sz)
    {
        RingTiles(sx, sz, width, height, out int x0, out int z0, out int x1, out int z1);
        return x0 <= x1 && z0 <= z1 && !RectHasReveal(sat, width, x0, z0, x1, z1);
    }

    // A cell may be switched off as a whole only if every renderer it owns stays inside its ring. Otherwise a hidden cell
    // could clip geometry drawn over revealed ground. Parts past the map edge are clamped to it: the ring is clamped the same
    // way, and beyond the map there is no tile a player could reveal.
    public static bool FootprintInRing(float minX, float maxX, float minZ, float maxZ, int sx, int sz, int width, int height)
    {
        if (!TileSpan(minX, maxX, width, 0, out int lx, out int hx)) return false;
        if (!TileSpan(minZ, maxZ, height, 0, out int lz, out int hz)) return false;
        lx = Math.Clamp(lx, 0, width - 1); hx = Math.Clamp(hx, 0, width - 1);
        lz = Math.Clamp(lz, 0, height - 1); hz = Math.Clamp(hz, 0, height - 1);
        RingTiles(sx, sz, width, height, out int x0, out int z0, out int x1, out int z1);
        return lx >= x0 && hx <= x1 && lz >= z0 && hz <= z1;
    }
}
