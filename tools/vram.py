import os, glob
res = {}
for p in glob.glob("/proc/[0-9]*"):
    seen = set(); tot = 0
    for f in glob.glob(p + "/fdinfo/*"):
        try: txt = open(f).read()
        except Exception: continue
        if "0000:03:00.0" not in txt: continue
        kv = dict(l.split(":",1) for l in txt.splitlines() if ":" in l)
        cid = kv.get("drm-client-id","").strip()
        if cid in seen: continue
        seen.add(cid)
        tot += int(kv.get("drm-memory-vram","0 KiB").split()[0])
    if tot > 30000:
        try: comm = open(p + "/comm").read().strip()
        except Exception: comm = "?"
        res[p] = (tot // 1024, comm)
for p, (mb, c) in sorted(res.items(), key=lambda x: -x[1][0])[:15]: print(f"{mb:6d} MB  {p[6:]:>8} {c}")
