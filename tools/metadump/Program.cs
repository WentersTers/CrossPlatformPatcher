// metadump — generic .NET metadata + IL census tool (read-only, runs nothing).
// Modes:
//   metadump types <exe>                  : TypeDefs (ns, name, base, fields, methods)
//   metadump methods <exe> <typeSubstr>   : methods of matching types (name, sig, IL size)
//   metadump il <exe> <typeSubstr> <m>    : IL disassembly with token resolution
//   metadump callsites <exe> <nameSubstr> : callers of MemberRefs matching substr
// No product identifiers; all findings are metadata citations.
using System.Reflection;
using System.Reflection.Emit;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text;

static MetadataReader Reader(string path, out PEReader pe, out FileStream fs)
{
    fs = File.OpenRead(path);
    pe = new PEReader(fs);
    return pe.GetMetadataReader();
}

static string TypeName(MetadataReader r, EntityHandle h) => h.Kind switch
{
    HandleKind.TypeDefinition => FullName(r, r.GetTypeDefinition((TypeDefinitionHandle)h)),
    HandleKind.TypeReference => RefName(r, r.GetTypeReference((TypeReferenceHandle)h)),
    HandleKind.TypeSpecification => "spec!",
    _ => h.Kind.ToString(),
};

static string FullName(MetadataReader r, TypeDefinition d)
{
    string ns = r.GetString(d.Namespace), n = r.GetString(d.Name);
    return string.IsNullOrEmpty(ns) ? n : ns + "." + n;
}

static string RefName(MetadataReader r, TypeReference t)
{
    string ns = r.GetString(t.Namespace), n = r.GetString(t.Name);
    return string.IsNullOrEmpty(ns) ? n : ns + "." + n;
}

static string MemberParent(MetadataReader r, MemberReference m) => m.Parent.Kind switch
{
    HandleKind.TypeReference => RefName(r, r.GetTypeReference((TypeReferenceHandle)m.Parent)),
    HandleKind.TypeDefinition => FullName(r, r.GetTypeDefinition((TypeDefinitionHandle)m.Parent)),
    HandleKind.MethodDefinition => "methodparent!",
    _ => m.Parent.Kind.ToString(),
};

static int OperandSize(OpCode op, byte[] il, int pos)
{
    return op.OperandType switch
    {
        OperandType.InlineNone => 0,
        OperandType.ShortInlineI or OperandType.ShortInlineVar or OperandType.ShortInlineBrTarget or OperandType.ShortInlineR => 1,
        OperandType.InlineVar => 2,
        OperandType.InlineI or OperandType.InlineBrTarget or OperandType.InlineField or OperandType.InlineMethod or OperandType.InlineSig or OperandType.InlineString or OperandType.InlineTok or OperandType.InlineType or OperandType.ShortInlineR or OperandType.InlineR => 4,
        OperandType.InlineI8 or OperandType.InlineR => 8,
        OperandType.InlineSwitch => 4 + 4 * BitConverter.ToInt32(il, pos),
        _ => 0,
    };
}

static int ReadCompressed(byte[] b, ref int p)
{
    byte a = b[p];
    if ((a & 0x80) == 0) { p += 1; return a; }
    if ((a & 0xC0) == 0x80) { int v = ((a & 0x3F) << 8) | b[p + 1]; p += 2; return v; }
    int v2 = ((a & 0x1F) << 24) | (b[p + 1] << 16) | (b[p + 2] << 8) | b[p + 3]; p += 4; return v2;
}

static string SigType(MetadataReader r, byte[] b, ref int p)
{
    byte e = b[p++];
    return e switch
    {
        0x01 => "void", 0x02 => "bool", 0x03 => "char", 0x04 => "i1", 0x05 => "u1",
        0x06 => "i2", 0x07 => "u2", 0x08 => "i4", 0x09 => "u4", 0x0A => "i8",
        0x0B => "u8", 0x0C => "r4", 0x0D => "r8", 0x0E => "string", 0x1C => "object",
        0x18 => "i", 0x19 => "u",
        0x11 or 0x12 => ResolveH(r, CodedToken(e, ReadCompressed(b, ref p))),
        0x1D => SigType(r, b, ref p) + "[]",
        0x15 => "genericinst!",
        _ => $"elem0x{e:X2}",
    };
}

static EntityHandle CodedToken(byte elem, int coded)
{
    int tag = coded & 0x3, rid = coded >> 2;
    int table = elem == 0x12
        ? tag switch { 0 => 0x02, 1 => 0x01, 2 => 0x1B, _ => 0 }
        : tag switch { 0 => 0x02, 1 => 0x01, 2 => 0x1B, _ => 0 };
    return System.Reflection.Metadata.Ecma335.MetadataTokens.EntityHandle((table << 24) | rid);
}

static string FieldType(MetadataReader r, FieldDefinitionHandle fh)
{
    try
    {
        var f = r.GetFieldDefinition(fh);
        byte[] b = r.GetBlobBytes(f.Signature);
        int p = 1; // skip FIELD (0x06)
        var mods = new List<string>();
        while (p < b.Length && (b[p] == 0x1F || b[p] == 0x20)) { mods.Add(b[p] == 0x1F ? "req" : "opt"); p++; }
        return string.Join(" ", mods.Concat(new[] { SigType(r, b, ref p) }));
    }
    catch { return "<sig?>"; }
}

static string ResolveH(MetadataReader r, EntityHandle h)
{
    try
    {
        return h.Kind switch
        {
            HandleKind.MemberReference => $"{MemberParent(r, r.GetMemberReference((MemberReferenceHandle)h))}::{r.GetString(r.GetMemberReference((MemberReferenceHandle)h).Name)}",
            HandleKind.MethodDefinition => MName(r, (MethodDefinitionHandle)h),
            HandleKind.TypeDefinition => FullName(r, r.GetTypeDefinition((TypeDefinitionHandle)h)),
            HandleKind.TypeReference => RefName(r, r.GetTypeReference((TypeReferenceHandle)h)),
            HandleKind.FieldDefinition => FName(r, (FieldDefinitionHandle)h),
            HandleKind.UserString => "<userstring:see-ldstr>",
            HandleKind.MethodSpecification => "<methodspec>",
            HandleKind.TypeSpecification => "<typespec>",
            _ => $"<{h.Kind}>",
        };
    }
    catch { return "<badhandle>"; }
}

static string Resolve(MetadataReader r, int token)
{
    try { return ResolveH(r, System.Reflection.Metadata.Ecma335.MetadataTokens.EntityHandle(token)); }
    catch { return $"<badtoken>0x{token:X}"; }
}

static string MName(MetadataReader r, MethodDefinitionHandle h)
{
    var m = r.GetMethodDefinition(h);
    return $"{FullName(r, r.GetTypeDefinition(m.GetDeclaringType()))}::{r.GetString(m.Name)}";
}

static string FName(MetadataReader r, FieldDefinitionHandle h)
{
    var f = r.GetFieldDefinition(h);
    return $"{FullName(r, r.GetTypeDefinition(f.GetDeclaringType()))}::{r.GetString(f.Name)}";
}

static string UStr(MetadataReader r, UserStringHandle h)
{
    try
    {
        string s = r.GetUserString(h);
        bool printable = s.Length > 0 && s.Length <= 300 && s.All(c => c >= 0x20 && c <= 0x7E || c == ' ');
        return printable ? $"\"{s}\"" : $"<blob len={s.Length}>";
    }
    catch { return "<blob?>"; }
}

static byte[] GetIL(PEReader pe, MetadataReader r, MethodDefinitionHandle h)
{
    var m = r.GetMethodDefinition(h);
    if (m.RelativeVirtualAddress == 0) return Array.Empty<byte>();
    var body = pe.GetMethodBody(m.RelativeVirtualAddress);
    return body.GetILBytes();
}

// Disassemble one method; returns lines and (for callsites) records.
static List<string> Disasm(PEReader pe, MetadataReader r, MethodDefinitionHandle h)
{
    var lines = new List<string>();
    byte[] il = GetIL(pe, r, h);
    int p = 0;
    while (p < il.Length)
    {
        int off = p;
        short code;
        if (il[p] == 0xFE) { code = (short)(0xFE00 | il[p + 1]); p += 2; }
        else { code = il[p]; p += 1; }
        if (!Cache.OpMap.TryGetValue(code, out var op)) { lines.Add($"{off:X4}: <bad 0x{code:X}>"); break; }
        int osize = OperandSize(op, il, p);
        string extra = "";
        if (osize == 4 && (op.OperandType == OperandType.InlineMethod || op.OperandType == OperandType.InlineField || op.OperandType == OperandType.InlineString || op.OperandType == OperandType.InlineType || op.OperandType == OperandType.InlineTok || op.OperandType == OperandType.InlineSig))
        {
            int tok = BitConverter.ToInt32(il, p);
            string target = ((tok >> 24) & 0xFF) == 0x70
                ? UStr(r, System.Reflection.Metadata.Ecma335.MetadataTokens.UserStringHandle(tok & 0xFFFFFF))
                : Resolve(r, tok);
            extra = $" ; [0x{tok:X8}] " + target;
        }
        else if (osize == 4 && op.OperandType == OperandType.InlineBrTarget)
            extra = $" -> {off + (p - off) + osize + BitConverter.ToInt32(il, p):X4}";
        else if (osize == 1 && op.OperandType == OperandType.ShortInlineBrTarget)
            extra = $" -> {off + (p - off) + osize + (sbyte)il[p]:X4}";
        lines.Add($"{off:X4}: {op.Name}" + (osize > 0 && extra == "" ? $" 0x{BitConverter.ToString(il, p, Math.Min(osize, il.Length - p)).Replace("-", "")}" : extra));
        p += osize;
    }
    return lines;
}

if (args.Length < 2) { Console.WriteLine("usage: metadump <types|methods|il|callsites> <exe> [args]"); return 2; }
string mode = args[0], path = args[1];
using var fs = File.OpenRead(path);
using var pe = new PEReader(fs);
var reader = pe.GetMetadataReader();

if (mode == "types")
{
    foreach (var h in reader.TypeDefinitions)
    {
        var d = reader.GetTypeDefinition(h);
        string name = FullName(reader, d);
        string b = d.BaseType.IsNil ? "(none)" : TypeName(reader, d.BaseType);
        Console.WriteLine($"{name} | base={b} | fields={d.GetFields().Count} | methods={d.GetMethods().Count}");
    }
}
else if (mode == "methods")
{
    string sub = args.Length > 2 ? args[2] : "";
    foreach (var h in reader.TypeDefinitions)
    {
        var d = reader.GetTypeDefinition(h);
        string name = FullName(reader, d);
        if (!name.Contains(sub, StringComparison.OrdinalIgnoreCase)) continue;
        foreach (var mh in d.GetMethods())
        {
            var m = reader.GetMethodDefinition(mh);
            string sig = reader.GetString(m.Name);
            int ils = GetIL(pe, reader, mh).Length;
            Console.WriteLine($"{name}::{sig} | rva=0x{m.RelativeVirtualAddress:X} | il={ils}");
        }
    }
}
else if (mode == "il")
{
    string tsub = args[2], msub = args[3];
    foreach (var h in reader.TypeDefinitions)
    {
        var d = reader.GetTypeDefinition(h);
        string name = FullName(reader, d);
        if (!name.Contains(tsub, StringComparison.OrdinalIgnoreCase)) continue;
        foreach (var mh in d.GetMethods())
        {
            var m = reader.GetMethodDefinition(mh);
            string mn = reader.GetString(m.Name);
            if (!mn.Contains(msub, StringComparison.OrdinalIgnoreCase)) continue;
            Console.WriteLine($"## {name}::{mn}");
            foreach (var ln in Disasm(pe, reader, mh)) Console.WriteLine(ln);
        }
    }
}
else if (mode == "ilm")
{
    // disassemble one MethodDef by raw token, e.g. 0x06001234 (ASCII-safe citation)
    int tok = Convert.ToInt32(args[2], 16);
    var eh = System.Reflection.Metadata.Ecma335.MetadataTokens.EntityHandle(tok);
    var mh = (MethodDefinitionHandle)eh;
    var md = reader.GetMethodDefinition(mh);
    Console.WriteLine($"## {MName(reader, mh)} [0x{tok:X8}] il={GetIL(pe, reader, mh).Length}");
    foreach (var ln in Disasm(pe, reader, mh)) Console.WriteLine(ln);
}
else if (mode == "callsites")
{
    string sub = args[2];
    foreach (var h in reader.TypeDefinitions)
    {
        var d = reader.GetTypeDefinition(h);
        string tname = FullName(reader, d);
        foreach (var mh in d.GetMethods())
        {
            var lines = Disasm(pe, reader, mh);
            string prevLdstr = "";
            foreach (var ln in lines)
            {
                if (ln.Contains("ldstr"))
                {
                    int q = ln.IndexOf("; ");
                    prevLdstr = q >= 0 ? ln[(q + 2)..] : "?";
                }
                else if ((ln.Contains(" call ") || ln.Contains(": call") || ln.Contains("callvirt") || ln.Contains("newobj")) && ln.Contains(sub, StringComparison.OrdinalIgnoreCase))
                {
                    var m2 = reader.GetMethodDefinition(mh);
                    Console.WriteLine($"{tname}::{reader.GetString(m2.Name)} | {ln.Trim()} | prev-ldstr={prevLdstr}");
                }
                else if (!ln.Contains("ldstr")) { /* keep prevLdstr across 1-2 instrs */ }
            }
        }
    }
}
else if (mode == "fields")
{
    // field inventory: name + decoded field type (for hide-gate enumeration)
    string sub = args.Length > 2 ? args[2] : "";
    foreach (var h in reader.TypeDefinitions)
    {
        var d = reader.GetTypeDefinition(h);
        string name = FullName(reader, d);
        if (!name.Contains(sub, StringComparison.OrdinalIgnoreCase)) continue;
        foreach (var fh in d.GetFields())
        {
            var f = reader.GetFieldDefinition(fh);
            Console.WriteLine($"{name}::{reader.GetString(f.Name)} : {FieldType(reader, fh)}");
        }
    }
}
else if (mode == "spec")
{
    // resolve a MethodSpecification token, e.g. 0x2B000005 -> generic method + args
    int tok = Convert.ToInt32(args[2], 16);
    int rid = tok & 0xFFFFFF;
    var sh = System.Reflection.Metadata.Ecma335.MetadataTokens.MethodSpecificationHandle(rid);
    var spec = reader.GetMethodSpecification(sh);
    Console.WriteLine($"methodspechandle=0x{tok:X8} method={ResolveH(reader, spec.Method)} sigbytes={reader.GetBlobBytes(spec.Signature).Length}");
}
else { Console.WriteLine("unknown mode"); return 2; }
return 0;

static class Cache
{
    public static readonly Dictionary<short, OpCode> OpMap = typeof(OpCodes)
        .GetFields(BindingFlags.Public | BindingFlags.Static)
        .Where(f => f.FieldType == typeof(OpCode))
        .Select(f => (OpCode)f.GetValue(null)!)
        .ToDictionary(o => o.Value);
}
