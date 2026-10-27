using System;
using System.Collections.Generic;

namespace OldenPerf;

// Bounded even if reporting is disabled. All values are milliseconds.
internal sealed class SampleWindow
{
    readonly List<double> _samples = new(1024);
    const int Capacity = 65536;
    double _sum;
    public int Count => _samples.Count;
    public bool Add(double ms)
    {
        if (!double.IsFinite(ms) || ms < 0 || Count >= Capacity) return false;
        _samples.Add(ms); _sum += ms; return true;
    }
    public (int count, double mean, double p50, double p95, double p99, double max) Summary()
    {
        if (Count == 0) return default;
        _samples.Sort();
        double P(double q) => _samples[Math.Max(0, (int)Math.Ceiling(q * Count) - 1)];
        return (Count, _sum / Count, P(.5), P(.95), P(.99), _samples[^1]);
    }
    public void Clear() { _samples.Clear(); _sum = 0; }
}
