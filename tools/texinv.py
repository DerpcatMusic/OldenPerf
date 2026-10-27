# Inventory every Texture2D: dims, format, mips, readable, streaming flag, GPU bytes.
import sys, os, glob, csv, UnityPy
from UnityPy.enums import TextureFormat as TF
d = sys.argv[1]; out = sys.argv[2]
BPP = {  # bits per pixel
 "DXT1":4,"BC4":4,"DXT1Crunched":4,"DXT5":8,"BC5":8,"BC6H":8,"BC7":8,"DXT5Crunched":8,
 "RGBA32":32,"ARGB32":32,"BGRA32":32,"RGB24":32,"RGBAHalf":64,"RGBAFloat":128,"R8":8,"Alpha8":8,
 "R16":16,"RHalf":16,"RFloat":32,"RGHalf":32,"RG16":16,"RGB565":16,"RGBA4444":16,"ARGB4444":16,"RG32":32,"RGB9e5Float":32,"RGBA64":64,
}
files = sorted(glob.glob(d+"/sharedassets*.assets")+glob.glob(d+"/level*")+[d+"/resources.assets", d+"/globalgamemanagers.assets"])
files = [f for f in files if not f.endswith((".resS",".resource"))]
w = csv.writer(open(out,"w")); w.writerow(["file","name","w","h","fmt","mips","readable","streaming","dim","mb"])
for f in files:
    try: env = UnityPy.load(f)
    except Exception as e: print("skip", f, e); continue
    for o in env.objects:
        if o.type.name != "Texture2D": continue
        try: t = o.read_typetree(check_read=False)
        except Exception as e: continue
        fmt = TF(t["m_TextureFormat"]).name
        wd, ht, mips = t["m_Width"], t["m_Height"], t.get("m_MipCount",1)
        bpp = BPP.get(fmt, 32)
        base = wd*ht*bpp/8
        mb = base*(4/3 if mips>1 else 1)/2**20
        w.writerow([os.path.basename(f), t["m_Name"], wd, ht, fmt, mips, t.get("m_IsReadable"), t.get("m_StreamingMipmaps"), t.get("m_TextureDimension"), round(mb,3)])
print("done")
