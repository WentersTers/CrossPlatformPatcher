using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.Text;

// resdump — generic .NET manifest-resource table dump + equality check.
// Regression tool for patch pipelines (ISS-027 class failures):
//   Test 1: manifest resource NAME-SET equality pre/post (plus per-entry
//           size/hash/codepoint diff) — names must be byte-identical.
//   Test 2: linked-resource asmref target preservation (Implementation
//           targets must be preserved name-for-name).
// Usage:
//   resdump <fileA> <fileB>            dump both tables + diff
//   resdump --check <fileA> <fileB>    exit 0 iff tests 1+2 pass
// No product identifiers live in this tool; invocation recipes for specific
// products travel through the restricted channel (publishing policy rule 8).

static (long va, long raw) FindSection(PEHeaders headers, int rva)
{
    foreach (var s in headers.SectionHeaders)
        if (rva >= s.VirtualAddress && rva < s.VirtualAddress + Math.Max(s.VirtualSize, s.SizeOfRawData))
            return (s.VirtualAddress, s.PointerToRawData);
    throw new Exception($"RVA 0x{rva:X} not in any section");
}

static List<(string name, string cps, long size, string sha, string impl)> Dump(string path)
{
    var list = new List<(string, string, long, string, string)>();
    using var fs = File.OpenRead(path);
    using var pe = new PEReader(fs);
    var md = pe.GetMetadataReader();
    int resRva = pe.PEHeaders.CorHeader!.ResourcesDirectory.RelativeVirtualAddress;
    foreach (var h in md.ManifestResources)
    {
        var r = md.GetManifestResource(h);
        string name = md.GetString(r.Name);
        string cps = string.Join(" ", name.Select(c => ((int)c).ToString("X4")));
        string impl = "embedded";
        if (!r.Implementation.IsNil)
        {
            impl = r.Implementation.Kind switch
            {
                HandleKind.AssemblyFile => "file:" + md.GetString(md.GetAssemblyFile((AssemblyFileHandle)r.Implementation).Name),
                HandleKind.AssemblyReference => "asmref:" + md.GetString(md.GetAssemblyReference((AssemblyReferenceHandle)r.Implementation).Name),
                _ => r.Implementation.Kind.ToString()
            };
            list.Add((name, cps, -1, "-", impl));
            continue;
        }
        long rva = resRva + (long)r.Offset;
        var (va, raw) = FindSection(pe.PEHeaders, (int)rva);
        fs.Position = raw + (rva - va);
        var lenBuf = new byte[4];
        fs.ReadExactly(lenBuf);
        int len = BitConverter.ToInt32(lenBuf, 0);
        var data = new byte[len];
        fs.ReadExactly(data);
        list.Add((name, cps, len, Convert.ToHexString(SHA256.HashData(data)), impl));
    }
    return list;
}

bool check = args.Length > 0 && args[0] == "--check";
var files = args.Where(a => !a.StartsWith("--")).ToArray();
if (files.Length < 2) { Console.WriteLine("usage: resdump [--check] <fileA> <fileB>"); return 2; }

var A = Dump(files[0]);
var B = Dump(files[1]);
Console.WriteLine($"# A {files[0]} resources={A.Count}");
foreach (var (name, cps, size, sha, impl) in A.OrderBy(x => x.name))
    Console.WriteLine($"A | {name} | size={size} | sha256={sha} | impl={impl} | cps={cps}");
Console.WriteLine($"# B {files[1]} resources={B.Count}");
foreach (var (name, cps, size, sha, impl) in B.OrderBy(x => x.name))
    Console.WriteLine($"B | {name} | size={size} | sha256={sha} | impl={impl} | cps={cps}");

var da = A.ToDictionary(x => x.name, x => x);
var db = B.ToDictionary(x => x.name, x => x);
var onlyA = da.Keys.Where(k => !db.ContainsKey(k)).OrderBy(k => k).ToList();
var onlyB = db.Keys.Where(k => !da.ContainsKey(k)).OrderBy(k => k).ToList();
var changed = new List<string>();
foreach (var (name, ent) in da)
{
    if (!db.ContainsKey(name)) continue;
    var o = db[name];
    if (o.size != ent.size || o.sha != ent.sha || o.cps != ent.cps || o.impl != ent.impl)
    {
        changed.Add(name);
        Console.WriteLine($"CHANGED | {name}");
        Console.WriteLine($"  A: size={ent.size} sha={ent.sha} impl={ent.impl} cps={ent.cps}");
        Console.WriteLine($"  B: size={o.size} sha={o.sha} impl={o.impl} cps={o.cps}");
    }
}
foreach (var n in onlyA) Console.WriteLine($"ONLY-IN-A | {n}");
foreach (var n in onlyB)
{
    var e = db[n];
    Console.WriteLine($"ADDED | {n} | size={e.size} | sha256={e.sha} | impl={e.impl}");
}

bool namesEqual = onlyA.Count == 0 && changed.Count == 0; // B may ADD entries
bool implsPreserved = da.Where(kv => db.ContainsKey(kv.Key)).All(kv => db[kv.Key].impl == kv.Value.impl);
Console.WriteLine($"# SUMMARY names-preserved={namesEqual} impls-preserved={implsPreserved} added={onlyB.Count} changed={changed.Count}");

if (check)
{
    bool pass = namesEqual && implsPreserved;
    Console.WriteLine(pass
        ? "CHECK: PASS (name-set preservation — adds permitted and enumerated above)"
        : "CHECK: FAIL (name-set preservation violated)");
    return pass ? 0 : 1;
}
return 0;
