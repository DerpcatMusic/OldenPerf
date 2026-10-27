using System;

namespace OldenPerf;

struct DepthEquality
{
    public long Count, DifferentBits, Invalid;
    public float Min, Max;
    public void Add(float reference, float actual)
    {
        if (Count++ == 0) { Min = reference; Max = reference; }
        Min = Math.Min(Min, reference); Max = Math.Max(Max, reference);
        if (!float.IsFinite(reference) || !float.IsFinite(actual) || reference < 0 || reference > 1 || actual < 0 || actual > 1) Invalid++;
        if (BitConverter.SingleToInt32Bits(reference) != BitConverter.SingleToInt32Bits(actual)) DifferentBits++;
    }
    // A uniform clear is not evidence that the copy preserves geometry depth.
    public bool Equivalent => Count > 0 && DifferentBits == 0 && Invalid == 0 && Max - Min > 0.0001f;
}
