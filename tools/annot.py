# Print one method from the ISIL dump with call targets named. usage: annot.py <isil.txt> <method-name> [type-hint-file]
import sys, re
sym = {}
for l in open("/data/dev/workspace/homm-olden-perf/symbols.txt"):
    a, n = l.split(" ", 1); sym.setdefault(int(a, 16), n.strip().split("::", 1)[1])
txt = open(sys.argv[1]).read().split("\nMethod: ")
for blk in txt:
    head = blk.split("\n", 1)[0]
    if re.search(r"\b" + re.escape(sys.argv[2]) + r"\(", head):
        print("Method:", head)
        for line in blk.split("\n")[1:]:
            m = re.search(r"call (?:qword ptr )?0*([0-9A-F]+)h", line)
            if m and int(m.group(1), 16) in sym:
                line += "    ; " + sym[int(m.group(1), 16)]
            if line.strip().startswith(("call", "jmp")) or "; " in line:
                print(line)
        break
