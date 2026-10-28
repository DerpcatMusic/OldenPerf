using System;
using OldenPerf;

static void Check(bool ok, string message)
{
    if (!ok) throw new Exception(message);
}
var depth = new DepthEquality();
Check(!depth.Equivalent, "No readback must not authorize a format change");
depth.Add(1, 1); depth.Add(1, 1);
Check(!depth.Equivalent, "A flat clear is inconclusive");
depth.Add(0.25f, 0.25f);
Check(depth.Equivalent, "Matching varied full-precision depth");
depth.Add(0.5f, MathF.BitIncrement(0.5f));
Check(!depth.Equivalent && depth.DifferentBits == 1, "Even a one-ULP change must reject the optimization");
depth = new DepthEquality(); depth.Add(0, -0.0f); depth.Add(1, 1);
Check(!depth.Equivalent, "Signed-zero bit changes must also be detected");
depth = new DepthEquality(); depth.Add(0, 0); depth.Add(1, 1); depth.Add(float.NaN, float.NaN);
Check(!depth.Equivalent && depth.Invalid == 1, "Nonfinite readback must fail closed");
Console.WriteLine("PASS: strict float32 depth equality, non-flat evidence, one-ULP and signed-zero changes, invalid data");

{
// Fog culling: a renderer is hidden only when its tile footprint, grown by a margin, has no revealed tile.
Check(SectionCullState.IsSectionRoot("Section Root: 3,4") && !SectionCullState.IsSectionRoot("Section Root") && !SectionCullState.IsSectionRoot(null), "Only named section roots are considered");
// Tile t is centred at world t - size/2: on a 160 map, world [-79, -78] covers tiles 1..2.
Check(SectionCullState.TileRange(-79f, -78f, 160, 0, out int tl, out int th) && tl == 1 && th == 2, "A world span maps back to its tile range (world = tile - size/2)");
Check(SectionCullState.TileRange(-79f, -78f, 160, 1, out tl, out th) && tl == 0 && th == 3, "The margin grows the tile range on both sides");
Check(SectionCullState.TileRange(-72.5f, -72.5f, 160, 0, out tl, out th) && tl == 8 && th == 8, "Cell (0,0)'s centre from the game's bounds lies in its middle tiles");
Check(!SectionCullState.TileRange(79.2f, 79.6f, 160, 0, out _, out _) && !SectionCullState.TileRange(-81f, -79f, 160, 0, out _, out _) && !SectionCullState.TileRange(-79f, -78f, 160, 2, out _, out _), "A footprint reaching past the map edge is kept, not hidden");
Check(!SectionCullState.TileRange(float.NaN, 0f, 160, 0, out _, out _), "Non-finite bounds are kept");
// 4x3 grid with only tile (1,2) revealed (x = 1, z = 2).
var fogGrid = new bool[4 * 3];
fogGrid[2 * 4 + 1] = true;
var sat = new int[(4 + 1) * (3 + 1)];
SectionCullState.BuildSummedArea(fogGrid, 4, 3, sat);
Check(SectionCullState.RectHasReveal(sat, 4, 1, 2, 1, 2) && SectionCullState.RectHasReveal(sat, 4, 0, 0, 3, 2) && !SectionCullState.RectHasReveal(sat, 4, 0, 0, 3, 1) && !SectionCullState.RectHasReveal(sat, 4, 2, 0, 3, 2), "A rectangle reports a reveal only when it contains a revealed tile");
Console.WriteLine("PASS: fog culling tile ranges, margin, edge handling, and summed-area reveal queries");
}
{
// Hook classification: one detour per native entry, aliases covered by the detour they share, unknown entries fail closed.
var kinds = HookPlan.Classify(new long[] { 100, 200, 100, 300, 200 }, 0);
Check(kinds != null && kinds[0] == HookPlan.Kind.Primary && kinds[1] == HookPlan.Kind.Mutator && kinds[2] == HookPlan.Kind.Alias
    && kinds[3] == HookPlan.Kind.Mutator && kinds[4] == HookPlan.Kind.Alias, "Primary alias and duplicate mutator entries must not be detoured twice");
Check(HookPlan.Classify(new long[] { 100, 0, 300 }, 0) == null, "An unknown entry must fail the whole plan closed");
Check(HookPlan.Classify(new long[] { 0, 300 }, 0) == null, "An unknown primary must fail the plan closed");
var noPrimary = HookPlan.Classify(new long[] { 400, 400, 500 }, -1);
Check(noPrimary != null && noPrimary[0] == HookPlan.Kind.Mutator && noPrimary[1] == HookPlan.Kind.Alias && noPrimary[2] == HookPlan.Kind.Mutator, "Without a primary, duplicate view writers are aliases of the first");
Check(HookPlan.Classify(new long[] { 100 }, 1) == null && HookPlan.Classify(new long[0], -1) == null, "An out-of-range primary or empty plan must fail closed");
Check(HookPlan.Claim(new long[] { 610, 620 }), "A free set of entries must be claimed");
Check(!HookPlan.Claim(new long[] { 630, 620 }) && HookPlan.Claim(new long[] { 630 }), "A claim that overlaps an owned entry must refuse all of its entries");
Check(!HookPlan.Claim(new long[] { 640, 0 }) && !HookPlan.Claim(new long[0]), "Unknown or empty claims must be refused");
Console.WriteLine("PASS: hook classification (primary once, alias not detoured, unknown fails closed) and all-or-nothing claims");
}

{
// Combined meshes use shader vertex IDs, including mirrored winding. Removal and restoration must
// retain exact IDs/order, and a visibility change with the same index count still changes the stream.
ushort[] original = { 2, 0, 1, 2, 3, 0, 9, 10, 8, 5, 7, 6 };
var parts = new[] {
    new FogBatchIndices.Part(0, 6, -3, -3, -3, -3, true),
    new FogBatchIndices.Part(6, 3, 2, 2, 2, 2, true),
    new FogBatchIndices.Part(9, 3, 2, 2, 2, 2, false), // unbounded deformation: keep
};
Check(FogBatchIndices.Valid(original, parts, 11), "Contiguous complete model ranges are valid");
Check(!FogBatchIndices.Valid(original, parts, 10), "An index outside the generated vertex buffer must refuse capture");
var gap = (FogBatchIndices.Part[])parts.Clone(); gap[1] = gap[1] with { Start = 7 };
Check(!FogBatchIndices.Valid(original, gap, 11), "An unaccounted batch index must refuse capture");
Check(!FogBatchIndices.Valid(original, parts[..2], 11), "Missing trailing model indices must refuse capture");
var grid = new bool[12 * 12]; var sat = new int[13 * 13];
var output = new ushort[original.Length]; var hidden = new bool[parts.Length];
grid[3 * 12 + 3] = true;
SectionCullState.BuildSummedArea(grid, 12, 12, sat);
int kept = FogBatchIndices.Compact(original, parts, sat, 12, 12, output, hidden);
Check(kept == 9 && output.AsSpan(0, kept).SequenceEqual(new ushort[] { 2, 0, 1, 2, 3, 0, 5, 7, 6 }),
    "Only the fully fogged model's exact index range is removed; winding and vertex IDs survive");
Check(!hidden[0] && hidden[1] && !hidden[2], "Unbounded deformation is retained even under fog");
Array.Fill(grid, true); SectionCullState.BuildSummedArea(grid, 12, 12, sat);
kept = FogBatchIndices.Compact(original, parts, sat, 12, 12, output, hidden);
Check(kept == original.Length && output.AsSpan(0, kept).SequenceEqual(original), "Revealing the whole map restores the original index stream exactly");
Array.Fill(grid, false); SectionCullState.BuildSummedArea(grid, 12, 12, sat);
kept = FogBatchIndices.Compact(original, parts, sat, 12, 12, output, hidden);
Check(kept == 3 && output.AsSpan(0, kept).SequenceEqual(new ushort[] { 5, 7, 6 }), "All static covered models are removed while unknown models remain");
var edge = new[] { new FogBatchIndices.Part(0, 12, -6, -6, -6, -6, true) };
kept = FogBatchIndices.Compact(original, edge, sat, 12, 12, output, new bool[1]);
Check(kept == original.Length, "Map-edge geometry stays visible when its complete footprint cannot be proven covered");
var equalParts = new[] { parts[0] with { Count = 3 }, parts[1] with { Start = 3 } };
ushort[] equalIndices = { 2, 0, 1, 9, 10, 8 }; var equalHidden = new bool[2];
grid[3 * 12 + 3] = true; SectionCullState.BuildSummedArea(grid, 12, 12, sat);
kept = FogBatchIndices.Compact(equalIndices, equalParts, sat, 12, 12, output, equalHidden);
Check(kept == 3 && output.AsSpan(0, kept).SequenceEqual(new ushort[] { 2, 0, 1 }), "First source model is selected");
grid[3 * 12 + 3] = false; grid[8 * 12 + 8] = true; SectionCullState.BuildSummedArea(grid, 12, 12, sat);
kept = FogBatchIndices.Compact(equalIndices, equalParts, sat, 12, 12, output, equalHidden);
Check(kept == 3 && output.AsSpan(0, kept).SequenceEqual(new ushort[] { 9, 10, 8 }) && equalHidden[0] && !equalHidden[1],
    "Moving visibility between equal-size models changes the stream despite an unchanged triangle count");
Check(original.AsSpan().SequenceEqual(new ushort[] { 2, 0, 1, 2, 3, 0, 9, 10, 8, 5, 7, 6 }), "Original engine indices remain immutable");
Console.WriteLine("PASS: batch index validation, fog removal, exact reveal restoration, conservative edges/deformation, equal-count visibility changes");

ushort[] shaderIds = { 0, 0, 0, 1, 1, 1, 0, 0, 0 };
ushort[] reordered = { 8, 6, 7, 5, 3, 4, 2, 0, 1 };
var footprints = new System.Collections.Generic.Dictionary<ushort, FogBatchIndices.Footprint> {
    [0] = new(-3, -3, -3, -3, true),
    [1] = new(2, 2, 2, 2, true),
};
Check(FogBatchIndices.TryMap(reordered, shaderIds, footprints, out var mapped) && mapped.Length == 3 &&
    mapped[0].Start == 0 && mapped[1].Start == 3 && mapped[2].Start == 6 && FogBatchIndices.Valid(reordered, mapped, 9),
    "The actual shader instance map handles reordered vertices and noncontiguous appearances of a model");
Array.Fill(grid, false); grid[3 * 12 + 3] = true; SectionCullState.BuildSummedArea(grid, 12, 12, sat);
kept = FogBatchIndices.Compact(reordered, mapped, sat, 12, 12, output, new bool[mapped.Length]);
Check(kept == 6 && output.AsSpan(0, kept).SequenceEqual(new ushort[] { 8, 6, 7, 2, 0, 1 }),
    "Both ranges of the revealed model retain their original shader vertex IDs, while the covered model is removed");
Check(FogBatchIndices.TryMap(new ushort[] { 0, 3, 1 }, shaderIds, footprints, out var mixed) && !mixed[0].Cullable,
    "A triangle spanning distinct shader instances stays visible");
Check(FogBatchIndices.TryMap(new ushort[] { 3, 4, 5 }, shaderIds,
    new System.Collections.Generic.Dictionary<ushort, FogBatchIndices.Footprint>(), out var missing) && !missing[0].Cullable,
    "A missing source footprint preserves the range instead of rejecting or hiding the entire batch");
Check(!FogBatchIndices.TryMap(new ushort[] { 0, 1, 9 }, shaderIds, footprints, out _),
    "An index outside the shader instance map refuses capture");
Console.WriteLine("PASS: actual shader-instance mapping, reordered/noncontiguous model ranges, mixed and unknown model safety");

var ownerMemory = System.Runtime.InteropServices.Marshal.AllocHGlobal(64);
var dataMemory = System.Runtime.InteropServices.Marshal.AllocHGlobal(32);
try
{
    System.Runtime.InteropServices.Marshal.WriteIntPtr(ownerMemory, 16, dataMemory);
    System.Runtime.InteropServices.Marshal.WriteInt32(ownerMemory, 24, 12);
    Check(NativeBatchArray.TryRead(ownerMemory, 16, 16, out var ptr, out int length) && ptr == dataMemory && length == 12,
        "Embedded native-array header reads the pointer and signed length without generic boxing");
    Check(!NativeBatchArray.TryRead(ownerMemory, 16, 11, out _, out _), "An oversized native length refuses the buffer");
    System.Runtime.InteropServices.Marshal.WriteInt32(ownerMemory, 24, -1);
    Check(!NativeBatchArray.TryRead(ownerMemory, 16, 16, out _, out _), "A negative native length refuses the buffer");
    System.Runtime.InteropServices.Marshal.WriteInt32(ownerMemory, 24, 0);
    Check(!NativeBatchArray.TryRead(ownerMemory, 16, 16, out _, out _), "An empty native buffer refuses capture");
    System.Runtime.InteropServices.Marshal.WriteInt32(ownerMemory, 24, 12);
    System.Runtime.InteropServices.Marshal.WriteIntPtr(ownerMemory, 16, IntPtr.Zero);
    Check(!NativeBatchArray.TryRead(ownerMemory, 16, 16, out _, out _), "A null native data pointer refuses capture");
    Check(!NativeBatchArray.TryRead(IntPtr.Zero, 16, 16, out _, out _) && !NativeBatchArray.TryRead(ownerMemory, 0, 16, out _, out _),
        "Null owners and offsets inside the object header are refused before dereferencing");
}
finally
{
    System.Runtime.InteropServices.Marshal.FreeHGlobal(dataMemory);
    System.Runtime.InteropServices.Marshal.FreeHGlobal(ownerMemory);
}
Console.WriteLine("PASS: embedded native-array pointer/length layout and null, negative, empty, oversized guards");

Check(FogBatchIndices.TryDeformationPadding(0, .1f, 0, .1f, 100, out float px, out float pz) && px == 0 && pz == 0,
    "Shader defaults do not deform a model when the flags are off");
Check(FogBatchIndices.TryDeformationPadding(8, .1f, .5f, -.3f, 2, out px, out pz) && px == .1f && pz == .3f,
    "Flag sway is bounded by the absolute world-space power");
Check(FogBatchIndices.TryDeformationPadding(16, 100, 100, 100, 2, out px, out pz) && px >= .1024f && px < .103f && pz >= .256f && pz < .257f,
    "Tree sway follows height squared; inactive flag power does not inflate its envelope");
Check(FogBatchIndices.TryDeformationPadding(24, .1f, .5f, -.3f, 2, out px, out pz) && px >= .26f && pz >= .70f,
    "Combined flag/tree sway includes the flag's Y displacement before bounding the tree term");
for (int wi = -100; wi <= 100; wi++) for (int ti = 0; ti <= 100; ti++)
{
    double wind = wi / 100.0, tree = ti / 50.0;
    double y = 2 + .5 * wind;
    double dx = .1 * wind - .02 * .64 * y * y * tree;
    double dz = -.3 * wind - .05 * .64 * y * y * tree;
    Check(Math.Abs(dx) <= px && Math.Abs(dz) <= pz, "All sampled shader extrema must fit the conservative combined envelope");
}
Check(!FogBatchIndices.TryDeformationPadding(8, float.NaN, 0, 0, 2, out _, out _) &&
    !FogBatchIndices.TryDeformationPadding(16, 0, 0, 0, float.PositiveInfinity, out _, out _),
    "Nonfinite active deformation stays visible");
Console.WriteLine("PASS: actual shader flag gating, static defaults, flag/tree displacement bounds and combined extrema");
}

{
// Deep-fog cells: a 16x16-tile cell is switched off only if neither it nor any neighbour has a revealed tile.
Check(SectionCullState.CellsAcross(160) == 10 && SectionCullState.CellsAcross(161) == 11 && SectionCullState.CellsAcross(16) == 1, "Cells are 16 tiles, rounding up");
Check(SectionCullState.ParseCell("Section Root: 4, 3", out int cx, out int cz) && cx == 4 && cz == 3, "Cell coordinates come from the root name");
Check(!SectionCullState.ParseCell("Section Root: 4", out _, out _) && !SectionCullState.ParseCell("Section Root: -1, 2", out _, out _) && !SectionCullState.ParseCell("Root: 1, 2", out _, out _), "Malformed cell names fail closed");
SectionCullState.RingTiles(0, 0, 160, 160, out int rx0, out int rz0, out int rx1, out int rz1);
Check(rx0 == 0 && rz0 == 0 && rx1 == 31 && rz1 == 31, "The ring is clamped at the map edge");
SectionCullState.RingTiles(5, 5, 160, 160, out rx0, out rz0, out rx1, out rz1);
Check(rx0 == 64 && rz0 == 64 && rx1 == 111 && rz1 == 111, "An interior ring spans the cell plus one cell on every side");
var cellGrid = new bool[64 * 64];
cellGrid[40 * 64 + 40] = true; // tile (40, 40) lies in cell (2, 2)
var cellSat = new int[65 * 65];
SectionCullState.BuildSummedArea(cellGrid, 64, 64, cellSat);
Check(SectionCullState.CellInDeepFog(cellSat, 64, 64, 0, 0) && SectionCullState.CellInDeepFog(cellSat, 64, 64, 0, 3), "Cells two steps from the reveal are in deep fog");
Check(!SectionCullState.CellInDeepFog(cellSat, 64, 64, 2, 2) && !SectionCullState.CellInDeepFog(cellSat, 64, 64, 1, 1) && !SectionCullState.CellInDeepFog(cellSat, 64, 64, 3, 3), "The revealed cell and its neighbours stay on");
// World x in [-70, -60] covers tiles 10..20, inside cell (1, 1)'s ring 0..47.
Check(SectionCullState.FootprintInRing(-70f, -60f, -70f, -60f, 1, 1, 160, 160), "A footprint inside the ring qualifies");
Check(!SectionCullState.FootprintInRing(-30f, -25f, -70f, -60f, 1, 1, 160, 160), "A footprint reaching past the ring disqualifies the cell");
Check(SectionCullState.FootprintInRing(-85f, -75f, -80.7f, -79.5f, 0, 0, 160, 160), "A corner footprint past the map edge is clamped to the corner cell's ring");
Check(!SectionCullState.FootprintInRing(-85f, -75f, -10f, 0f, 0, 0, 160, 160), "Clamping one axis does not excuse the other");
Check(!SectionCullState.FootprintInRing(-1f, 1f, -1f, 1f, 0, 0, 160, 160), "Map-centre geometry disqualifies a corner cell");
Console.WriteLine("PASS: deep-fog cells, cell naming, ring clamping, and cell eligibility footprints");

// Texture budget: desired + headroom, clamped; unknown demand never starves.
Check(TextureBudgetMath.Target(2000, 384, 1024, 7168) == 2384, "Budget is desired plus headroom");
Check(TextureBudgetMath.Target(100, 384, 1024, 7168) == 1024 && TextureBudgetMath.Target(9000, 384, 1024, 7168) == 7168, "Budget is clamped to floor and ceiling");
Check(TextureBudgetMath.Target(0, 384, 1024, 7168) == 7168 && TextureBudgetMath.Target(float.NaN, 384, 1024, 7168) == 7168, "Unknown demand uses the ceiling");
Console.WriteLine("PASS: texture budget tracks desired memory with headroom and never starves on unknown demand");
}
