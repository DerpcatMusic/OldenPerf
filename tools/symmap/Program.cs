// Emit "0xVA name" for every method Cpp2IL tagged with [Address(RVA=..)] (image base 0x180000000).
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
const ulong Base = 0x180000000;
foreach (var path in Directory.GetFiles(args[0], "*.dll"))
{
    using var pe = new PEReader(File.OpenRead(path));
    var md = pe.GetMetadataReader();
    string asm = Path.GetFileNameWithoutExtension(path);
    foreach (var th in md.TypeDefinitions)
    {
        var t = md.GetTypeDefinition(th);
        string tn = md.GetString(t.Name);
        var decl = t.GetDeclaringType();
        while (!decl.IsNil) { var d = md.GetTypeDefinition(decl); tn = md.GetString(d.Name) + "+" + tn; t = d; decl = d.GetDeclaringType(); }
        string ns = md.GetString(md.GetTypeDefinition(th).Namespace); if (ns.Length == 0) ns = md.GetString(t.Namespace);
        string full = (ns.Length > 0 ? ns + "." : "") + tn;
        foreach (var mh in md.GetTypeDefinition(th).GetMethods())
        {
            var m = md.GetMethodDefinition(mh);
            foreach (var ah in m.GetCustomAttributes())
            {
                var a = md.GetCustomAttribute(ah);
                if (a.Constructor.Kind != HandleKind.MethodDefinition && a.Constructor.Kind != HandleKind.MemberReference) continue;
                string rva = null;
                try {
                var blob = md.GetBlobReader(a.Value);
                if (blob.Length < 4) continue;
                blob.ReadUInt16();
                ushort nnamed = 0;
                // Address has no ctor args; named props follow.
                nnamed = blob.ReadUInt16();
                for (int i = 0; i < nnamed; i++)
                {
                    blob.ReadByte(); var ty = blob.ReadSerializationTypeCode(); string pn = blob.ReadSerializedString();
                    string v = ty == SerializationTypeCode.String ? blob.ReadSerializedString() : null;
                    if (v == null) break;
                    if (pn == "RVA") rva = v;
                }
                } catch { continue; }
                if (rva == null) continue;
                Console.WriteLine($"0x{Base + Convert.ToUInt64(rva, 16):x} {asm}::{full}.{md.GetString(m.Name)}");
                break;
            }
        }
    }
}
