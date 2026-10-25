// Dumper de metadados .NET sem decompilador: le o PE e as tabelas do metadata
// direto (System.Reflection.Metadata). Serve para auditar um binario gerenciado
// com uma fonte independente do ILSpy/IDA.
//
// Uso:
//   dotnet run --project scripts/pe_metadata_dump -- <assembly> summary
//   ... types | pinvoke | refs | resources | strings | res
//
// Por que existe (caso ISLC): o IDA 9.0 entrega o IL, o ILSpy entrega o C#; este
// dump entrega a MESMA contagem de metodos/tipos por outro caminho, a superficie
// completa de P/Invoke, as referencias de assembly e os recursos embutidos - que
// nem o .asm do IDA nem o projeto decompilado mostram de forma completa.

using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;

if (args.Length < 1)
{
    Console.Error.WriteLine("uso: pe_metadata_dump <assembly> <summary|types|pinvoke|refs|resources|strings|res>");
    return 1;
}

var path = args[0];
var mode = args.Length > 1 ? args[1] : "summary";

using var fs = File.OpenRead(path);
using var pe = new PEReader(fs);
if (!pe.HasMetadata)
{
    Console.WriteLine("NO_METADATA (binario nativo ou resource-only)");
    return 2;
}
var mr = pe.GetMetadataReader();

string TypeName(TypeDefinitionHandle h)
{
    var td = mr.GetTypeDefinition(h);
    var ns = mr.GetString(td.Namespace);
    var n = mr.GetString(td.Name);
    return string.IsNullOrEmpty(ns) ? n : ns + "." + n;
}

switch (mode)
{
    case "summary":
    {
        var asm = mr.GetAssemblyDefinition();
        var hdr = pe.PEHeaders;
        Console.WriteLine($"file            : {Path.GetFileName(path)}");
        Console.WriteLine($"size_bytes      : {new FileInfo(path).Length}");
        Console.WriteLine($"assembly        : {mr.GetString(asm.Name)} {asm.Version}");
        Console.WriteLine($"mvid            : {mr.GetGuid(mr.GetModuleDefinition().Mvid)}");
        Console.WriteLine($"machine         : {hdr.CoffHeader.Machine}");
        Console.WriteLine($"subsystem       : {hdr.PEHeader?.Subsystem}");
        Console.WriteLine($"cor_flags       : {hdr.CorHeader?.Flags}");
        Console.WriteLine($"pe_timestamp    : {DateTimeOffset.FromUnixTimeSeconds(hdr.CoffHeader.TimeDateStamp).UtcDateTime:u}");
        Console.WriteLine($"typdefs         : {mr.TypeDefinitions.Count}");
        Console.WriteLine($"methoddefs      : {mr.MethodDefinitions.Count}");
        Console.WriteLine($"fielddefs       : {mr.FieldDefinitions.Count}");
        Console.WriteLine($"memberrefs      : {mr.MemberReferences.Count}");
        Console.WriteLine($"assemblyrefs    : {mr.AssemblyReferences.Count}");
        Console.WriteLine($"modulerefs      : {mr.GetTableRowCount(TableIndex.ModuleRef)}");
        Console.WriteLine($"typerefs        : {mr.TypeReferences.Count}");
        Console.WriteLine($"manifestres     : {mr.ManifestResources.Count}");
        Console.WriteLine($"customattrs     : {mr.CustomAttributes.Count}");
        Console.WriteLine($"md_size_bytes   : {hdr.CorHeader?.MetadataDirectory.Size}");
        break;
    }
    case "types":
    {
        foreach (var h in mr.TypeDefinitions)
        {
            var td = mr.GetTypeDefinition(h);
            if (mr.GetString(td.Name) == "<Module>") continue;
            int methods = 0, pins = 0, fields = 0;
            foreach (var m in td.GetMethods())
            {
                methods++;
                if (!mr.GetMethodDefinition(m).GetImport().Name.IsNil) pins++;
            }
            foreach (var _ in td.GetFields()) fields++;
            Console.WriteLine($"{TypeName(h)}\tmethods={methods}\tfields={fields}\tpinvoke={pins}\tattrs=0x{(int)td.Attributes:X}");
        }
        break;
    }
    case "pinvoke":
    {
        // Superficie nativa completa: modulo!entrypoint, metodo gerenciado que declara,
        // tipo declarante e flags de marshalling. Nenhum outro dump mostra isso inteiro.
        foreach (var h in mr.MethodDefinitions)
        {
            var md = mr.GetMethodDefinition(h);
            var imp = md.GetImport();
            if (imp.Name.IsNil) continue;
            var decl = mr.GetTypeDefinition(md.GetDeclaringType());
            var mod = mr.GetModuleReference(imp.Module);
            var ns = mr.GetString(decl.Namespace);
            Console.WriteLine($"{mr.GetString(mod.Name)}!{mr.GetString(imp.Name)}\t{mr.GetString(md.Name)}\t{(string.IsNullOrEmpty(ns) ? "" : ns + ".")}{mr.GetString(decl.Name)}\t{imp.Attributes}");
        }
        break;
    }
    case "refs":
    {
        foreach (var h in mr.AssemblyReferences)
        {
            var a = mr.GetAssemblyReference(h);
            Console.WriteLine($"{mr.GetString(a.Name)}\t{a.Version}");
        }
        break;
    }
    case "resources":
    {
        foreach (var h in mr.ManifestResources)
        {
            var r = mr.GetManifestResource(h);
            Console.WriteLine($"{mr.GetString(r.Name)}\t{(r.Implementation.IsNil ? "embedded" : "linked")}\t0x{r.Offset:X}\t{r.Attributes}");
        }
        break;
    }
    case "strings":
    {
        // User string heap: literais ldstr do codigo (nao pega strings de .resources).
        var h = MetadataTokens.UserStringHandle(0);
        while (true)
        {
            try { h = mr.GetNextHandle(h); } catch { break; }
            if (h.IsNil) break;
            string s;
            try { s = mr.GetUserString(h); } catch { break; }
            if (s.Length == 0) continue;
            var one = s.Replace("\r", "\\r").Replace("\n", "\\n").Replace("\t", "\\t");
            Console.WriteLine($"0x{MetadataTokens.GetHeapOffset(h):X6}\t{one}");
        }
        break;
    }
    case "res":
    {
        // Conteudo dos .resources embutidos (inclusive BAML do WPF e strings localizadas).
        // Usa o proprio loader do runtime para materializar o stream: mapear RVA na mao
        // retorna bytes deslocados e o ResourceReader acusa "Corrupt .resources file".
        var asm = System.Runtime.Loader.AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.GetFullPath(path));
        foreach (var rh in mr.ManifestResources)
        {
            var res = mr.GetManifestResource(rh);
            var name = mr.GetString(res.Name);
            if (!name.EndsWith(".resources")) continue;
            Console.WriteLine($"### {name}");
            using var raw = asm.GetManifestResourceStream(name);
            if (raw is null) { Console.WriteLine("NO_STREAM"); continue; }
            using var ms = new MemoryStream();
            raw.CopyTo(ms);
            ms.Position = 0;
            using var reader = new System.Resources.ResourceReader(ms);
            var e = reader.GetEnumerator();
            while (true)
            {
                System.Collections.DictionaryEntry entry;
                // Icone/imagem binaria pode nao materializar com ResourceReader do .NET Core;
                // aborta a lista aqui em vez de derrubar o dump inteiro.
                try
                {
                    if (!e.MoveNext()) break;
                    entry = e.Entry;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"ABORT\t{ex.GetType().Name}: {ex.Message}");
                    break;
                }
                if (entry.Value is string sv)
                    Console.WriteLine($"STR\t{entry.Key}\t{sv.Replace("\r", "\\r").Replace("\n", "\\n")}");
                else if (entry.Value is Stream st)
                    Console.WriteLine($"STREAM\t{entry.Key}\t{st.Length} bytes");
                else
                    Console.WriteLine($"OBJ\t{entry.Key}\t{entry.Value?.GetType().Name}");
            }
        }
        break;
    }
    default:
        Console.Error.WriteLine($"modo invalido: {mode}");
        return 1;
}

return 0;
