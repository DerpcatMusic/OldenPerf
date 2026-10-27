import sys, UnityPy, json
d = sys.argv[1]
env = UnityPy.load(d + "/globalgamemanagers", d + "/globalgamemanagers.assets")
for o in env.objects:
    t = o.type.name
    if t == "QualitySettings":
        try: print(json.dumps(o.read_typetree(check_read=False), indent=1, default=str))
        except Exception as e: print("QS err", e)
    if t == "MonoBehaviour":
        try:
            tr = o.read_typetree(check_read=False)
        except Exception as e:
            continue
        name = tr.get("m_Name", "")
        if any(k in str(list(tr.keys())) for k in ("m_RendererDataList", "m_RenderPipelineSettings", "m_SupportsHDR")):
            print("== RP asset", o.path_id, name)
            print(json.dumps({k:v for k,v in tr.items() if not isinstance(v,(list,dict)) or k in ("m_RenderPipelineSettings",)}, indent=1, default=str)[:15000])
