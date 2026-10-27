import sys, UnityPy, json
env = UnityPy.load(sys.argv[1] + "/globalgamemanagers")
for o in env.objects:
    t = o.type.name
    if t in ("QualitySettings", "PlayerSettings", "GraphicsSettings"):
        tree = o.read_typetree(check_read=False)
        if t == "PlayerSettings":
            tree = {k: v for k, v in tree.items() if any(s in k.lower() for s in ("gpu","graphic","thread","vsync","frame","stream","mip","texture","color","jobs","incremental","gc","fullscreen","resolution","virtual","dx12","d3d","api"))}
        print("==", t); print(json.dumps(tree, indent=1, default=str)[:12000])
