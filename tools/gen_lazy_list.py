# Names of Resources textures under icons/ (eagerly LoadAll'd by the icon registry), minus names that also exist elsewhere.
import json, os, csv, collections
paths = json.load(open("res_paths.json"))
icon, other = set(), set()
for ps in paths.values():
    for p in ps:
        (icon if p.startswith("icons/") else other).add(os.path.basename(p))
names = collections.Counter(r["name"] for r in csv.DictReader(open("tex.csv")))
lazy = sorted(n for n in icon - other if names[n] <= 1)
open("OldenPerf/lazy_textures.txt", "w").write("\n".join(lazy) + "\n")
print(len(icon), "icon names,", len(lazy), "unique lazy names")
