import sys, UnityPy
d = sys.argv[1]
env = UnityPy.load(d + "/globalgamemanagers.assets")
f = list(env.files.values())[0]
print("typetrees:", f._enable_type_tree)
o = f.objects[8216]
print(o.type.name, o.byte_size)
ms = o.parse_as_object() if hasattr(o,'parse_as_object') else o.read()
print(ms)
from collections import Counter
print(Counter(x.type.name for x in f.objects.values()).most_common(20))
