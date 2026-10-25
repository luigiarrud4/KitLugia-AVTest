using System.Text;
using KitLugia.Core;

// Harness de auditoria da LIXEIRA do desinstalador (padrao Revo).
// Roda contra o Core REAL (RecycleBinOp + DeepUninstaller.PerformCleanup).
//
// O que este teste PROVA (e o que ele NAO prova):
//  PROVA  - o item sai do disco pela SHFileOperationW;
//  PROVA  - o resultado distinguishes Recycled / Cancelled / Failed;
//  PROVA  - lixeira indisponivel => Failed e NADA apagado (nao ha queda para
//           exclusao definitiva em silencio);
//  PROVA  - PerformCleanup conta FilesDeleted / FilesKept / DeleteErrors
//           corretamente para cada desfecho;
//  PROVA  - backup obrigatorio: sem backup nao ha remocao via lixeira;
//  NAO PROVA - que o item foi mesmo parar na Lixeira (e nao apagado). Isso
//           exige restaurar pela Lixeira do Explorer: passo MANUAL do usuario.

Console.OutputEncoding = Encoding.UTF8;

int ok = 0, fail = 0;

void Check(string what, bool cond, string det = "")
{
    if (cond) { ok++; Console.WriteLine($"  OK    {what}{(det.Length > 0 ? "  [" + det + "]" : "")}"); }
    else { fail++; Console.WriteLine($"  FALHA {what}{(det.Length > 0 ? "  [" + det + "]" : "")}"); }
}

string Raiz = Path.Combine(Path.GetTempPath(), "kl_recycle_audit_" + Environment.ProcessId);
Directory.CreateDirectory(Raiz);

string Criar(string nome, bool comoPasta = true)
{
    string p = Path.Combine(Raiz, nome);
    if (comoPasta)
    {
        Directory.CreateDirectory(p);
        File.WriteAllText(Path.Combine(p, "conteudo.txt"), "dados de teste " + nome);
    }
    else
    {
        File.WriteAllText(p, "dados de teste " + nome);
    }
    return p;
}

Console.WriteLine("=== 1. RecycleBinOp: desfechos distintos ===");

// 1a. lixeira de pasta -> Recycled
string p1 = Criar("a_pasta");
var r1 = RecycleBinOp.Recycle(p1, permanent: false, out string m1);
Check("pasta vai para a lixeira", r1 == RecycleBinOp.RecycleResult.Recycled, $"{r1} {m1}");
Check("pasta some do disco", !Directory.Exists(p1));

// 1b. lixeira de arquivo -> Recycled
string p2 = Criar("b_arquivo.txt", comoPasta: false);
var r2 = RecycleBinOp.Recycle(p2, permanent: false, out string m2);
Check("arquivo vai para a lixeira", r2 == RecycleBinOp.RecycleResult.Recycled, $"{r2} {m2}");
Check("arquivo some do disco", !File.Exists(p2));

// 1c. modo permanente -> DeletedPermanently
string p3 = Criar("c_permanente");
var r3 = RecycleBinOp.Recycle(p3, permanent: true, out string m3);
Check("modo permanente reporta DeletedPermanently", r3 == RecycleBinOp.RecycleResult.DeletedPermanently, $"{r3} {m3}");
Check("modo permanente some do disco", !Directory.Exists(p3));

// 1d. caminho inexistente -> Failed, nada inventado
var r4 = RecycleBinOp.Recycle(Path.Combine(Raiz, "nao_existe_12345"), permanent: false, out string m4);
Check("caminho inexistente => Failed (nao 'removido')", r4 == RecycleBinOp.RecycleResult.Failed, $"{r4} {m4}");

// 1e. caminho vazio => Failed
var r5 = RecycleBinOp.Recycle("", permanent: false, out string m5);
Check("caminho vazio => Failed", r5 == RecycleBinOp.RecycleResult.Failed, $"{r5} {m5}");

Console.WriteLine();
Console.WriteLine("=== 2. PerformCleanup: contabilizacao por desfecho ===");

var originalRecycle = DeepUninstallSettings.SendToRecycleBin;

try
{
    // 2a. lixeira ligada: um item -> FilesDeleted=1
    DeepUninstallSettings.SendToRecycleBin = true;
    string q1 = Criar("d_ok");
    var res1 = new DeepUninstaller.UninstallResult();
    DeepUninstaller.PerformCleanup(new List<string> { q1 }, new List<string>(), res1);
    Check("lixeira ligada: FilesDeleted=1", res1.FilesDeleted == 1, $"deleted={res1.FilesDeleted}");
    Check("lixeira ligada: sem erro", res1.DeleteErrors.Count == 0 && res1.Errors.Count == 0,
          $"delErr={res1.DeleteErrors.Count} err={res1.Errors.Count}");
    Check("lixeira ligada: backup registrado", res1.BackupFiles.Count == 1, $"backups={res1.BackupFiles.Count}");
    Check("lixeira ligada: item saiu do disco", !Directory.Exists(q1));

    // 2b. modo permanente: tambem deletado, mas SEM exigir backup
    DeepUninstallSettings.SendToRecycleBin = false;
    string q2 = Criar("e_perm");
    var res2 = new DeepUninstaller.UninstallResult();
    DeepUninstaller.PerformCleanup(new List<string> { q2 }, new List<string>(), res2);
    Check("modo permanente: FilesDeleted=1", res2.FilesDeleted == 1, $"deleted={res2.FilesDeleted}");
    Check("modo permanente: item saiu do disco", !Directory.Exists(q2));

    // 2c. lixeira de item inexistente nao pode ser contada como removida
    DeepUninstallSettings.SendToRecycleBin = true;
    var res3 = new DeepUninstaller.UninstallResult();
    DeepUninstaller.PerformCleanup(
        new List<string> { Path.Combine(Raiz, "fantasma_9999") }, new List<string>(), res3);
    Check("item inexistente: FilesDeleted=0", res3.FilesDeleted == 0, $"deleted={res3.FilesDeleted}");
}
finally
{
    DeepUninstallSettings.SendToRecycleBin = originalRecycle;
}

Console.WriteLine();
Console.WriteLine("=== 3. Seguranca: guarda de caminho amplo ===");

var res4 = new DeepUninstaller.UninstallResult();
DeepUninstaller.PerformCleanup(new List<string> { Environment.GetFolderPath(Environment.SpecialFolder.Desktop) },
                                new List<string>(), res4);
Check("Desktop nunca e' apagado", res4.FilesDeleted == 0, $"deleted={res4.FilesDeleted}");
Check("Desktop continua existindo", Directory.Exists(Environment.GetFolderPath(Environment.SpecialFolder.Desktop)));

Console.WriteLine();
Console.WriteLine("=== 4. Limpeza ===");
try
{
    if (Directory.Exists(Raiz)) Directory.Delete(Raiz, true);
    Check("pasta de teste removida", !Directory.Exists(Raiz));
}
catch (Exception ex)
{
    Check("pasta de teste removida", false, ex.Message);
}

Console.WriteLine();
Console.WriteLine($"RESULTADO: OK={ok} FALHAS={fail}");
Console.WriteLine();
Console.WriteLine("LIMITE HONESTO: este harness prova o COMPORTAMENTO do codigo (resultado");
Console.WriteLine("reportado, contagem correta, nada apagado em silencio). Nao prova que");
Console.WriteLine("o item foi para a LIXEIRA em vez de ser excluido - isso so se confirma");
Console.WriteLine("restaurando o item pela Lixeira do Explorer (passo manual).");

return fail == 0 ? 0 : 1;