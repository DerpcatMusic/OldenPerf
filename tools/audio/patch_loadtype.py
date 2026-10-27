# In-place AudioClip load-type patch: DecompressOnLoad (0) -> CompressedInMemory (1). Edits only the 4-byte m_LoadType
# of each clip; verifies the bytes after it match the typetree (channels, frequency) before writing. File size unchanged.
# Usage: python3 -I patch_loadtype.py <resources.assets copy to patch in place> <report.json>
import sys, json, struct, UnityPy
path, report = sys.argv[1], sys.argv[2]
env = UnityPy.load(path)
sf = next(iter(env.files.values()))
endian = '<' if getattr(sf.reader, 'endian', '<') == '<' else '>'
edits, skipped = [], []
for o in env.objects:
    if o.type.name != 'AudioClip': continue
    t = o.read_typetree()
    if t['m_LoadType'] != 0: continue
    name = t['m_Name'].encode('utf-8')
    off = o.byte_start + 4 + len(name)
    off += (-off + o.byte_start) % 4  # strings are 4-aligned relative to the object start
    edits.append((o.path_id, t['m_Name'], off, t['m_Channels'], t['m_Frequency']))
with open(path, 'r+b') as f:
    for pid, name, off, ch, fr in edits:
        f.seek(off); lt, c2, f2 = struct.unpack(endian + 'iii', f.read(12))
        if (lt, c2, f2) != (0, ch, fr): skipped.append((pid, name, lt, c2, f2)); continue
        f.seek(off); f.write(struct.pack(endian + 'i', 1))
json.dump({'patched': len(edits) - len(skipped), 'skipped': skipped, 'edits': [(p, n, o) for p, n, o, _, _ in edits]}, open(report, 'w'))
print('patched', len(edits) - len(skipped), 'skipped', len(skipped))
