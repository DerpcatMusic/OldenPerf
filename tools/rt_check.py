# Round-trip check: re-serialize unmodified Texture2D/Sprite objects and compare bytes exactly.
import sys, UnityPy
env = UnityPy.load(sys.argv[1])
res = {}
for o in env.objects:
    tn = o.type.name
    if tn not in ("Texture2D", "Sprite"): continue
    before = bytes(o.get_raw_data())
    o.save_typetree(o.read_typetree(check_read=True))
    after = bytes(o.get_raw_data())
    k = res.setdefault(tn, [0, 0]); k[0 if before == after else 1] += 1
    if before != after and k[1] < 3: print("DIFF", tn, len(before), len(after))
print({t: f"ok={v[0]} bad={v[1]}" for t, v in res.items()})
