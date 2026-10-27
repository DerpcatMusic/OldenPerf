import sys, UnityPy, json, os
d = sys.argv[1]
env = UnityPy.load(d + "/globalgamemanagers")
f = list(env.files.values())[0]
for i, e in enumerate(f.externals): print(i + 1, e.path)
