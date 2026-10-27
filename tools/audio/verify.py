# Compare two asset files object by object: report differing object types, and AudioClip fields that differ.
import sys, hashlib, collections, UnityPy
a, b = (UnityPy.load(p) for p in sys.argv[1:3])
A = {o.path_id: o for o in a.objects}; B = {o.path_id: o for o in b.objects}
diff = collections.Counter(); fields = collections.Counter()
for pid, oa in A.items():
    ob = B[pid]
    if oa.get_raw_data() == ob.get_raw_data(): continue
    diff[oa.type.name] += 1
    if oa.type.name == 'AudioClip':
        ta, tb = oa.read_typetree(), ob.read_typetree()
        for k in ta:
            if ta[k] != tb[k]: fields[(k, str(ta[k]), str(tb[k]))] += 1
print('objects', len(A), len(B), 'differing by type', dict(diff))
print('AudioClip field changes', dict(fields))
