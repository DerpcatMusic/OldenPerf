using System.Collections.Generic;
using System.Linq;

namespace OldenPerf;

// Decides which managed methods get a Harmony detour, from each method's resolved native entry address.
// A detour is installed per native entry, so two managed methods that resolve to one routine must not both be
// detoured (the second detour would overwrite the first one's trampoline). No Unity types: ProfilerChecks runs this.
//   Primary - the culling pass itself. Detoured once with prefix, postfix and finalizer.
//   Mutator - a bxm or bzf method at its own entry. Detoured with a prefix that invalidates the cache.
//   Alias   - shares an entry with the primary or an earlier mutator. Not detoured: every call to it already runs the
//             detour of that entry, so it is covered without a second patch.
// Any entry that is unknown (0) fails the whole plan (null). An unresolved method could be the primary in disguise.
public static class HookPlan
{
    public enum Kind { Primary, Mutator, Alias }

    static readonly HashSet<long> Claimed = new();

    // addresses[primary] is the culling pass; pass -1 when there is none. Returns null on any unknown entry.
    public static Kind[] Classify(long[] addresses, int primary)
    {
        if (addresses.Length == 0 || (primary >= 0 && primary >= addresses.Length)) return null;
        if (addresses.Any(a => a == 0)) return null;
        var kinds = new Kind[addresses.Length];
        var seen = new HashSet<long>();
        if (primary >= 0)
        {
            kinds[primary] = Kind.Primary;
            seen.Add(addresses[primary]);
        }
        for (int i = 0; i < addresses.Length; i++)
        {
            if (i == primary) continue;
            kinds[i] = seen.Add(addresses[i]) ? Kind.Mutator : Kind.Alias;
        }
        return kinds;
    }

    // Claims every entry for one owner, or none. Refuses an entry another hook already holds, so one physical target
    // gets a single detour across the audit and cache modules.
    public static bool Claim(IEnumerable<long> entries)
    {
        var list = entries.Distinct().ToList();
        if (list.Count == 0 || list.Any(e => e == 0 || Claimed.Contains(e))) return false;
        foreach (var e in list) Claimed.Add(e);
        return true;
    }
}
