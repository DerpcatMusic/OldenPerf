using System;

namespace OldenPerf;

// Pure, unit-tested: the budget the controller asks for.
static class TextureBudgetMath
{
    public static float Target(float desiredMb, float headroomMb, float floorMb, float ceilingMb)
    {
        if (!float.IsFinite(desiredMb) || desiredMb <= 0) return ceilingMb; // unknown demand: never starve
        return Math.Clamp(desiredMb + headroomMb, floorMb, ceilingMb);
    }
}
