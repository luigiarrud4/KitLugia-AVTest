using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Threading;

namespace KitLugia.Core;

/// <summary>
/// O que o usuário pediu para fazer com o caminho.
/// </summary>
public enum GuaranteedAction
{
    /// <summary>Liberar o caminho (fechar handles/matar processo/driver) sem deletar.</summary>
    ForceStop,
    /// <summary>Assumir a propriedade (dono + Administradores:F).</summary>
    TakeOwnership,
    /// <summary>Liberar + deletar (com fallback de remoção no próximo boot).</summary>
    Delete,
}

public sealed class GuaranteeStep
{
    public int Index { get; set; }
    public string Message { get; set; } = "";
    public bool Ok { get; set; }
}

/// <summary>
/// Resultado do pipeline "sempre funciona" (<see cref="FileOpGuarantee"/>).
/// </summary>
public sealed class GuaranteeResult
{
    /// <summary>Objetivo atingido — agora ou agendado para o próximo boot.</summary>
    public bool Ok { get; set; }
    /// <summary>Deleção agendada via PendingFileRenameOperations (será feita no reboot).</summary>
    public bool ScheduledForReboot { get; set; }
    /// <summary>O caminho continua bloqueado (handle de processo que não pode ser finalizado).</summary>
    public bool StillBlocked { get; set; }
    /// <summary>Faltou privilégio de administrador para concluir.</summary>
    public bool NeedsAdmin { get; set; }
    public int ProcessesKilled { get; set; }
    public int HandlesClosed { get; set; }
    public int ItemsOwned { get; set; }
    public int Deleted { get; set; }
    public int Failed { get; set; }
    public List<GuaranteeStep> Steps { get; } = new();
    public List<string> Errors { get; } = new();
    public string Summary { get; set; } = "";

    /// <summary>Soma 1 aos contadores sem estourar (usado pelos helpers).</summary>
    public void AddStep(string message, bool ok)
    {
        Steps.Add(new GuaranteeStep { Index = Steps.Count + 1, Message = message, Ok = ok });
    }

    // ── Serialização (worker elevado ↔ processo de UI) ──────────────────────
    // Formato de linha: "S|1|mensagem", "R|CHAVE|valor", "E|erro".
    // O separador de campo é '|' — qualquer '|' na mensagem é trocado por '¦'.

    public IEnumerable<string> ToWire()
    {
        yield return "R|OK|" + (Ok ? 1 : 0);
        yield return "R|SCHEDULED|" + (ScheduledForReboot ? 1 : 0);
        yield return "R|BLOCKED|" + (StillBlocked ? 1 : 0);
        yield return "R|NEEDSADMIN|" + (NeedsAdmin ? 1 : 0);
        yield return "R|KILLED|" + ProcessesKilled;
        yield return "R|HANDLES|" + HandlesClosed;
        yield return "R|OWNED|" + ItemsOwned;
        yield return "R|DELETED|" + Deleted;
        yield return "R|FAILED|" + Failed;
        yield return "R|SUMMARY|" + Clean(Summary);
        foreach (var s in Steps)
            yield return $"S|{(s.Ok ? 1 : 0)}|{Clean(s.Message)}";
        foreach (var e in Errors)
            yield return "E|" + Clean(e);
    }

    private static string Clean(string? s) => (s ?? "").Replace('|', '¦').Replace("\r", " ").Replace("\n", " ");

    public static GuaranteeResult FromWire(IEnumerable<string> lines)
    {
        var r = new GuaranteeResult();
        foreach (var raw in lines)
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;
            var parts = raw.Split('|', 3);
            try
            {
                switch (parts[0])
                {
                    case "S":
                        if (parts.Length >= 3) r.AddStep(parts[2], parts[1] == "1");
                        break;
                    case "E":
                        if (parts.Length >= 2) r.Errors.Add(parts[1]);
                        break;
                    case "R":
                        if (parts.Length < 3) break;
                        switch (parts[1])
                        {
                            case "OK": r.Ok = parts[2] == "1"; break;
                            case "SCHEDULED": r.ScheduledForReboot = parts[2] == "1"; break;
                            case "BLOCKED": r.StillBlocked = parts[2] == "1"; break;
                            case "NEEDSADMIN": r.NeedsAdmin = parts[2] == "1"; break;
                            case "KILLED": int.TryParse(parts[2], out int k); r.ProcessesKilled = k; break;
                            case "HANDLES": int.TryParse(parts[2], out int h); r.HandlesClosed = h; break;
                            case "OWNED": int.TryParse(parts[2], out int o); r.ItemsOwned = o; break;
                            case "DELETED": int.TryParse(parts[2], out int d); r.Deleted = d; break;
                            case "FAILED": int.TryParse(parts[2], out int f); r.Failed = f; break;
                            case "SUMMARY": r.Summary = parts[2]; break;
                        }
                        break;
                }
            }
            catch { /* linha corrompida não derruba o parse */ }
        }
        return r;
    }
}

/// <summary>
/// Pipeline "sempre funciona" para Force Stop / Take Ownership / Delete.
///
/// Por que existe: antes, cada ação da página fazia UMA coisa (matar processos OU assumir
/// dono OU deletar) e parava no primeiro obstáculo — pedindo ao usuário para relançar
/// elevado, ou devolvendo "falhou" quando a ACL negava / o handle ficava aberto.
/// Aqui a ordem é sempre a mesma e nenhuma etapa é opcional:
///
///   0. Habilita TODOS os privilégios necessários (Debug + TakeOwnership + Backup + Restore + LoadDriver)
///   1. Valida o caminho com probe NATIVO (distingue "não existe" de "ACL nega")
///   2. Destrava o acesso: assume a propriedade quando o item nega leitura/escrita
///   3. Fecha handles/mata bloqueadores/descarrega driver (motor do ForceStopUnlockService)
///   4. Executa a ação pedida
///   5. Nunca devolve beco sem saída: na ação **Delete**, se ainda estiver bloqueado, agenda a
///      remoção no próximo boot (PendingFileRenameOperations) e diz isso claramente. Na ação
///      **ForceStop** ("liberar") NÃO se agenda nada — o resultado é reportado como ainda
///      travado, para nunca apagar um item que o usuário só queria destravar.
///
/// Todo passo é reportado (callback) para a UI mostrar o que está acontecendo.
/// </summary>
[SupportedOSPlatform("windows")]
public static class FileOpGuarantee
{
    // ── P/Invoke de privilégios ─────────────────────────────────────────────
    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(IntPtr processHandle, uint desiredAccess, out IntPtr tokenHandle);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool LookupPrivilegeValue(string? systemName, string name, out LUID luid);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool AdjustTokenPrivileges(IntPtr tokenHandle, bool disableAll,
        ref TOKEN_PRIVILEGES newState, int bufferLength, IntPtr previousState, IntPtr returnLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateFileW(string lpFileName, uint dwDesiredAccess, uint dwShareMode,
        IntPtr lpSecurityAttributes, uint dwCreationDisposition, uint dwFlagsAndAttributes, IntPtr hTemplateFile);

    private const uint TOKEN_ADJUST_PRIVILEGES = 0x0020;
    private const uint TOKEN_QUERY = 0x0008;
    private const int SE_PRIVILEGE_ENABLED = 0x0002;
    private const uint GENERIC_READ = 0x80000000;
    private const uint GENERIC_WRITE = 0x40000000;
    private const uint FILE_SHARE_ALL = 0x00000007;
    private const uint FILE_SHARE_NONE = 0x00000000;
    private const uint OPEN_EXISTING = 3;
    private const uint FILE_ATTRIBUTE_NORMAL = 0x80;
    private const uint FILE_FLAG_BACKUP_SEMANTICS = 0x02000000;
    private const int INVALID_HANDLE_VALUE = -1;
    private const int ERROR_SHARING_VIOLATION = 32;
    private const int ERROR_ACCESS_DENIED = 5;
    private const int ERROR_NOT_READY = 21;

    // LUID é { DWORD LowPart; LONG HighPart; } com alinhamento 4 — um `long` aqui (alinhado
    // a 8) empurraria o campo para o offset 8 e o Windows leria UM LUID INVÁLIDO em
    // AdjustTokenPrivileges (retorna TRUE + ERROR_NOT_ALL_ASSIGNED 1300 = privilégio
    // "não atribuído"), sem habilitar nada. Estrutura separada = layout nativo exato (16 bytes).
    [StructLayout(LayoutKind.Sequential)]
    private struct LUID { public uint LowPart; public int HighPart; }

    [StructLayout(LayoutKind.Sequential)]
    private struct TOKEN_PRIVILEGES
    {
        public uint PrivilegeCount;
        public LUID Luid;
        public uint Attributes;
    }

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool GetTokenInformation(IntPtr tokenHandle, int tokenInformationClass,
        out TOKEN_ELEVATION tokenInformation, int tokenInformationLength, out int returnLength);

    [StructLayout(LayoutKind.Sequential)]
    private struct TOKEN_ELEVATION { public int TokenIsElevated; }

    private const int TokenElevationClass = 20;

    /// <summary>
    /// Elevação UAC DE VERDADE (TokenElevation) — o teste definitivo. `IsInRole(Administrator)`
    /// pode dizer "true" com um token filtrado (UAC ligado, processo não elevado); nesse caso a
    /// operação em item protegido falha mesmo o app "achando" que é admin. É este método que
    /// decide se precisamos subir o worker elevado.
    /// </summary>
    public static bool IsElevated()
    {
        try
        {
            if (!OpenProcessToken(GetCurrentProcess(), TOKEN_QUERY, out var token)) return false;
            try
            {
                return GetTokenInformation(token, TokenElevationClass, out TOKEN_ELEVATION e,
                           Marshal.SizeOf<TOKEN_ELEVATION>(), out _)
                       && e.TokenIsElevated != 0;
            }
            finally { CloseHandle(token); }
        }
        catch { return false; }
    }

    private static readonly string[] AllPrivileges =
    {
        "SeDebugPrivilege",
        "SeTakeOwnershipPrivilege",
        "SeBackupPrivilege",
        "SeRestorePrivilege",
        "SeLoadDriverPrivilege",
    };

    /// <summary>
    /// Habilita todos os privilégios usados pelo fluxo. Sem admin, só uma parte entra no
    /// token (o resto fica "não atribuído") — por isso devolvemos a lista dos que entraram.
    /// </summary>
    public static List<string> EnableAllPrivileges()
    {
        var enabled = new List<string>();
        try
        {
            if (!OpenProcessToken(GetCurrentProcess(), TOKEN_ADJUST_PRIVILEGES | TOKEN_QUERY, out var token))
            {
                Logger.Log($"[GUARANTEE] OpenProcessToken falhou (erro {Marshal.GetLastWin32Error()})");
                return enabled;
            }
            try
            {
                foreach (var priv in AllPrivileges)
                {
                    if (!LookupPrivilegeValue(null, priv, out LUID luid))
                    {
                        Logger.Log($"[GUARANTEE] {priv}: LookupPrivilegeValue falhou (erro {Marshal.GetLastWin32Error()})");
                        continue;
                    }

                    var tp = new TOKEN_PRIVILEGES { PrivilegeCount = 1, Luid = luid, Attributes = SE_PRIVILEGE_ENABLED };
                    bool ok = AdjustTokenPrivileges(token, false, ref tp, 0, IntPtr.Zero, IntPtr.Zero);
                    int err = Marshal.GetLastWin32Error();

                    // O contrato do AdjustTokenPrivileges: TRUE + GetLastError()==ERROR_SUCCESS
                    // significa que o privilégio entrou. TRUE + ERROR_NOT_ALL_ASSIGNED (1300)
                    // significa "o token não tem esse privilégio" — conta como não habilitado.
                    if (ok && err == 0) enabled.Add(priv);
                    else Logger.Log($"[GUARANTEE] {priv}: ok={ok} erro={err} (1300 = não atribuído ao token)");
                }
            }
            finally { CloseHandle(token); }
        }
        catch (Exception ex)
        {
            Logger.Log($"[GUARANTEE] EnableAllPrivileges erro: {ex.Message}");
        }
        Logger.Log($"[GUARANTEE] Privilégios habilitados: {(enabled.Count == 0 ? "nenhum" : string.Join(", ", enabled))}");
        return enabled;
    }

    // ── Probes de acesso (não alteram nada) ─────────────────────────────────

    /// <summary>Abre o item para leitura+escrita com compartilhamento total (teste fiel de DACL).</summary>
    private static bool CanAccess(string path, bool isDir)
    {
        try
        {
            uint flags = FILE_ATTRIBUTE_NORMAL | (isDir ? FILE_FLAG_BACKUP_SEMANTICS : 0);
            IntPtr h = CreateFileW(path, GENERIC_READ | GENERIC_WRITE, FILE_SHARE_ALL,
                IntPtr.Zero, OPEN_EXISTING, flags, IntPtr.Zero);
            if (h.ToInt64() == INVALID_HANDLE_VALUE)
            {
                int err = Marshal.GetLastWin32Error();
                // 32 (sharing violation) = a ACL permite, só tem alguém usando.
                return err == ERROR_SHARING_VIOLATION;
            }
            CloseHandle(h);
            return true;
        }
        catch { return true; }
    }

    /// <summary>Testa se ainda existe um handle EXCLUSIVO aberto no arquivo (lock real).</summary>
    private static bool IsLockedNow(string path, bool isDir)
    {
        try
        {
            uint flags = FILE_ATTRIBUTE_NORMAL | (isDir ? FILE_FLAG_BACKUP_SEMANTICS : 0);
            IntPtr h = CreateFileW(path, GENERIC_READ | GENERIC_WRITE, FILE_SHARE_NONE,
                IntPtr.Zero, OPEN_EXISTING, flags, IntPtr.Zero);
            if (h.ToInt64() == INVALID_HANDLE_VALUE)
            {
                int err = Marshal.GetLastWin32Error();
                return err == ERROR_SHARING_VIOLATION || err == ERROR_ACCESS_DENIED || err == ERROR_NOT_READY;
            }
            CloseHandle(h);
            return false;
        }
        catch { return false; }
    }

    private static bool CanEnumerate(string dir)
    {
        try
        {
            // MoveNext é obrigatório: EnumerateFileSystemEntries é preguiçoso e a ACL só é
            // exercitada quando a enumeração realmente abre o diretório (sem isso, pasta
            // totalmente negada passaria como acessível).
            using var e = Directory.EnumerateFileSystemEntries(dir).GetEnumerator();
            e.MoveNext();
            return true;
        }
        catch { return false; }
    }

    /// <summary>Teto de arquivos verificados por pasta no teste de lock (pastas enormes).</summary>
    private const int MaxLockProbeFiles = 512;

    /// <summary>
    /// O caminho ainda tem bloqueio ATIVO? Arquivo: um único teste exclusivo (share NONE).
    /// Pasta: amostra os arquivos internos (até <see cref="MaxLockProbeFiles"/>) — um handle
    /// aberto em qualquer um deles conta como travado.
    ///
    /// Por que pasta importa: o Force Stop só testava arquivo (`!isDir &amp;&amp; !IsLockedNow`),
    /// então TODA pasta caía no ramo de "ainda bloqueada" — mesmo livre — e ia para o
    /// agendamento de remoção no boot (que apaga o caminho). Ver o bloco do ForceStop em Run.
    /// </summary>
    private static bool HasActiveLock(string target, bool isDir, out int probed)
    {
        probed = 0;
        if (!isDir)
        {
            probed = 1;
            return IsLockedNow(target, false);
        }

        try
        {
            foreach (var file in Directory.EnumerateFiles(target, "*", SearchOption.AllDirectories))
            {
                if (probed >= MaxLockProbeFiles) break;
                probed++;
                if (IsLockedNow(file, false)) return true;
            }
        }
        catch (Exception ex)
        {
            Logger.Log($"[GUARANTEE] Falha ao verificar locks da pasta: {ex.Message}");
            return true; // conservador: sem certeza, não afirma "liberado"
        }
        return false;
    }

    /// <summary>Raiz de volume ("E:\", "C:") — não se toma posse de raiz de volume.</summary>
    private static bool IsVolumeRoot(string path)
    {
        string p = path.TrimEnd('\\', '/');
        return p.Length == 2 && p[1] == ':'; // "C:" / "E:"
    }

    /// <summary>Normaliza a entrada: tira aspas/espaços e resolve caminhos longos.</summary>
    public static string NormalizePath(string path)
    {
        string p = (path ?? "").Trim().Trim('"');
        if (p.Length == 0) return p;
        if (p.StartsWith(@"\\?\", StringComparison.Ordinal)) return p;
        if (p.StartsWith(@"\\", StringComparison.Ordinal)) return p; // UNC: não prefixa (já é longo-ok na prática)
        // \\?\ desliga o parsing de ".."/"."/nomes reservados e permite > MAX_PATH.
        if (p.Length >= 240) return @"\\?\" + p;
        return p;
    }

    // ── Pipeline ───────────────────────────────────────────────────────────

    /// <summary>
    /// Executa o pipeline completo. Nunca lança: qualquer exceção vira erro estruturado.
    /// </summary>
    public static GuaranteeResult Run(string path, GuaranteedAction action, bool recursive,
        bool grantFullControl, Action<GuaranteeStep>? onStep = null, CancellationToken ct = default)
    {
        var r = new GuaranteeResult();
        void Step(string msg, bool ok)
        {
            r.AddStep(msg, ok);
            Logger.Log($"[GUARANTEE] {(ok ? "OK  " : "FAIL")} {msg}");
            try { onStep?.Invoke(r.Steps[^1]); } catch { }
        }

        try
        {
            string target = NormalizePath(path);
            if (target.Length == 0)
            {
                Step("Caminho vazio.", false);
                r.Summary = "Informe um arquivo ou pasta.";
                return r;
            }

            // Elevação real (TokenElevation) — não a heurística do grupo Administradores.
            bool admin = IsElevated();
            if (admin != SystemUtils.IsRunningAsAdministrator())
                Logger.Log($"[GUARANTEE] Atenção: IsInRole(Admin)={SystemUtils.IsRunningAsAdministrator()} " +
                           $"mas TokenElevation={admin} — usando TokenElevation.");

            // ── 0. Privilégios ──────────────────────────────────────────────
            var privs = EnableAllPrivileges();
            Step($"Privilégios habilitados: {(privs.Count == 0 ? "nenhum" : string.Join(", ", privs))}" +
                 (admin ? "" : " (sem elevação — itens protegidos podem exigir administrador)"),
                 admin || privs.Count >= 3);

            // ── 1. Validação do caminho ─────────────────────────────────────
            bool exists = File.Exists(target) || Directory.Exists(target);
            bool isDir = Directory.Exists(target);
            if (!exists)
            {
                int probe = FileTakeOwnership.ProbePath(target, out bool pExists, out bool pIsDir, out int errCode);
                if (!pExists)
                {
                    Step($"Caminho não encontrado (erro {probe}).", false);
                    r.Errors.Add($"Caminho não encontrado: {target}");
                    r.Failed++;
                    r.Summary = "Caminho não encontrado.";
                    return r;
                }
                // Existe mas a ACL nega até o probe — tratamos como pasta/arquivo negado.
                exists = true;
                isDir = pIsDir;
                Step($"Item existe mas a ACL nega leitura (erro {errCode}) — seguindo com acesso privilegiado.", true);
            }

            // Raiz de volume ("C:\", "E:\") não é alvo válido para liberar/deletar: o motor varreria
            // o disco inteiro (registrando cada arquivo no log) sem poder destravar a unidade.
            // TakeOwnership tem tratamento próprio mais abaixo (e nunca mexe no dono de um volume).
            if (IsVolumeRoot(target) && action != GuaranteedAction.TakeOwnership)
            {
                Step("Raiz de volume não é um alvo válido — selecione um arquivo ou pasta específica.", false);
                r.Summary = "Selecione um arquivo ou pasta (a raiz de uma unidade não é liberável).";
                return r;
            }

            // ── 2. Destravar o acesso (ownership) ───────────────────────────
            bool accessOk = isDir ? CanEnumerate(target) : CanAccess(target, false);
            // Raiz de volume: NUNCA mexer no dono/DACL (probe de escrita falha aí por design e
            // trocar o dono de um volume inteiro é efeito colateral grave).
            bool needOwnership = !IsVolumeRoot(target)
                                 && (action == GuaranteedAction.TakeOwnership || !accessOk);
            if (needOwnership)
            {
                Step(action == GuaranteedAction.TakeOwnership
                    ? "Assumindo propriedade (dono + Administradores)..."
                    : "Acesso negado pela ACL — assumindo propriedade antes de continuar...", true);

                var own = FileTakeOwnership.TakeOwn(target, recursive, (done, total, _) =>
                {
                    if (total > 0 && (done == total || done % 250 == 0))
                        Step($"Propriedade: {done}/{total} item(ns)...", true);
                }, grantFullControl);
                r.ItemsOwned = own.Success;
                r.Failed += own.Failed;
                foreach (var e in own.Errors) r.Errors.Add(e);
                Step($"Propriedade assumida em {own.Success}/{own.Total} item(ns)" +
                     (own.Failed > 0 ? $" — {own.Failed} falha(s)" : "") +
                     (own.FallbackUsed ? " (fallback takeown/icacls usado)" : ""), own.Failed == 0);
                if (own.Failed > 0)
                    r.NeedsAdmin |= !admin;
            }

            if (action == GuaranteedAction.TakeOwnership)
            {
                bool okNow = isDir ? CanEnumerate(target) : CanAccess(target, false);
                if (IsVolumeRoot(target))
                    Step("Raiz de volume — a posse de uma unidade inteira não é alterada por este recurso.", true);
                r.Ok = okNow;
                if (!okNow) { r.NeedsAdmin |= !admin; r.Errors.Add("A propriedade foi alterada, mas o acesso ainda é negado."); }
                Step(okNow ? "Acesso confirmado: o item já pode ser editado/deletado." : "Ownership aplicado, mas o acesso continua negado.", okNow);
                r.Summary = okNow
                    ? $"{r.ItemsOwned} item(ns) agora são seus — pode editar/deletar."
                    : "Ownership aplicado, mas o acesso continua negado (tente novamente como administrador).";
                return r;
            }

            // ── 3. Bloqueadores (processos/handles/drivers) ─────────────────
            var blockers = ForceStopUnlockService.FindBlockingProcesses(target);
            if (blockers.Count > 0)
            {
                Step($"{blockers.Count} bloqueador(es) encontrado(s) — fechando handles e liberando...", true);
                var unlock = ForceStopUnlockService.Unlock(target, blockers, deleteTarget: false);
                r.ProcessesKilled += unlock.ProcessesKilled;
                r.HandlesClosed += unlock.HandlesClosed;
                foreach (var e in unlock.Errors) r.Errors.Add(e);
                Step($"Liberação: {unlock.ProcessesKilled} processo(s) finalizado(s), {unlock.HandlesClosed} handle(s) fechado(s).", unlock.Success);
            }
            else
            {
                Step("Nenhum processo bloqueador encontrado.", true);
            }

            // ── 4. Ação pedida ──────────────────────────────────────────────
            if (action == GuaranteedAction.ForceStop)
            {
                bool locked = HasActiveLock(target, isDir, out int probed);
                if (!locked)
                {
                    r.Ok = true;
                    Step(isDir
                        ? $"Pasta liberada: nenhum dos {probed} arquivo(s) verificados está travado."
                        : "Caminho liberado: nenhum handle ativo restante.", true);
                }
                else
                {
                    // Segunda passada: novos bloqueadores podem ter aparecido (serviços que
                    // reabrem o arquivo, driver que recarrega) — o motor é idempotente.
                    var again = ForceStopUnlockService.FindBlockingProcesses(target)
                        .Where(b => !SystemProcessNamesSafe.Contains(b.ProcessName)).ToList();
                    if (again.Count > 0)
                    {
                        var unl2 = ForceStopUnlockService.Unlock(target, again, deleteTarget: false);
                        r.ProcessesKilled += unl2.ProcessesKilled;
                        r.HandlesClosed += unl2.HandlesClosed;
                        foreach (var e in unl2.Errors) r.Errors.Add(e);
                        Step($"Segunda passada: {again.Count} bloqueador(es) tratado(s).", unl2.Success);
                    }

                    locked = HasActiveLock(target, isDir, out probed);
                    if (!locked)
                    {
                        r.Ok = true;
                        Step(isDir
                            ? $"Pasta liberada após a segunda passada ({probed} arquivo(s) verificados)."
                            : "Caminho liberado após a segunda passada.", true);
                    }
                    else
                    {
                        // ⚠ FORCE STOP É LIBERAR, NÃO DELETAR (corrigido em 29/09).
                        // Antes, este ramo chamava ScheduleDeleteOnReboot — que registra o
                        // caminho em PendingFileRenameOperations para ser APAGADO no próximo
                        // boot. Numa pasta o ramo era SEMPRE alcançado (o teste de lock ignorava
                        // pastas), então clicar em "Liberar Selecionados" numa pasta agendava a
                        // exclusão dela. Agora o Force Stop é honesto: reporta que ainda há
                        // bloqueio e não mexe em nada. Quem quer remover usa a ação Delete (que
                        // mantém o agendamento no boot como último recurso).
                        r.StillBlocked = true;
                        r.Ok = false;
                        Step(isDir
                            ? $"Ainda há arquivo travado dentro da pasta ({probed} verificado(s)) e nenhum processo pode ser finalizado (processo de sistema/driver crítico). Use 'Tentar Deletar' para agendar a remoção no próximo boot."
                            : "Ainda há handle ativo e nenhum processo pode ser finalizado (processo de sistema/driver crítico). Use 'Tentar Deletar' para agendar a remoção no próximo boot.", false);
                    }
                }
            }
            else // Delete
            {
                if (isDir)
                {
                    var (deleted, failed, errors) = ForceStopUnlockService.RobustDeleteFolder(target);
                    r.Deleted = deleted;
                    r.Failed += failed;
                    foreach (var e in errors) r.Errors.Add(e);
                    bool gone = !Directory.Exists(target) && !File.Exists(target);
                    if (gone)
                    {
                        r.Ok = true;
                        Step($"Pasta removida ({deleted} arquivo(s)).", true);
                    }
                    else
                    {
                        bool scheduled = DriverUnlockService.ScheduleDeleteOnReboot(target);
                        r.ScheduledForReboot = scheduled;
                        r.Ok = scheduled;
                        Step(scheduled
                            ? "Pasta ainda em uso: remoção agendada para o próximo boot."
                            : "Não foi possível remover a pasta.", scheduled);
                    }
                }
                else
                {
                    var (ok, method, error) = ForceStopUnlockService.RobustDeleteWithRetry(target);
                    bool gone = !File.Exists(target);
                    bool isRebootMethod = string.Equals(method, "reboot_delete", StringComparison.OrdinalIgnoreCase);
                    if (gone) r.Deleted = 1;
                    if (!gone && !isRebootMethod && !string.IsNullOrEmpty(error)) r.Errors.Add(error);
                    r.ScheduledForReboot = !gone && isRebootMethod;
                    r.Ok = gone || isRebootMethod;
                    if (gone)
                        Step($"Arquivo removido (método: {method}).", true);
                    else if (isRebootMethod)
                        Step("Arquivo em uso: remoção agendada para o próximo boot.", true);
                    else
                    {
                        bool scheduled = DriverUnlockService.ScheduleDeleteOnReboot(target);
                        r.ScheduledForReboot = scheduled;
                        r.Ok = scheduled;
                        Step(scheduled
                            ? "Nenhum método deletou agora: remoção agendada para o próximo boot."
                            : $"Falha ao remover ({method}): {error}", scheduled);
                    }
                }
            }

            if (!r.Ok) r.NeedsAdmin |= !admin;
            r.Summary = BuildSummary(r, action, Path.GetFileName(target.TrimEnd('\\', '/')) is { Length: > 0 } n ? n : target);
            return r;
        }
        catch (Exception ex)
        {
            Logger.Log($"[GUARANTEE] Exceção: {ex}");
            r.Errors.Add(ex.Message);
            r.Failed++;
            r.Summary = "Erro inesperado: " + ex.Message;
            Step("Erro inesperado: " + ex.Message, false);
            return r;
        }
    }

    /// <summary>Processos de sistema que o motor nunca mata (espelho do ForceStopUnlockService).</summary>
    private static readonly HashSet<string> SystemProcessNamesSafe = new(StringComparer.OrdinalIgnoreCase)
    {
        "System", "Idle", "Registry", "smss", "csrss", "wininit", "winlogon",
        "lsass", "services", "svchost", "dwm", "fontdrvhost", "sihost",
        "taskhostw", "RuntimeBroker", "ShellExperienceHost", "SearchUI",
        "ctfmon", "csrsrv", "msdtc", "WmiPrvSE", "msmpeng", "Nisr",
        "MsMpEng", "MpCmdRun"
    };

    private static string BuildSummary(GuaranteeResult r, GuaranteedAction action, string name)
    {
        if (action == GuaranteedAction.Delete)
        {
            if (r.ScheduledForReboot) return "Ainda em uso — agendado para o próximo boot.";
            return r.Ok ? "Removido com sucesso." : "Não foi possível remover.";
        }

        if (r.StillBlocked && r.ScheduledForReboot)
            return "Handle ativo — operação agendada para o próximo boot.";
        if (r.StillBlocked)
            return "Ainda travado: handle ativo de processo/driver de sistema (não pode ser finalizado).";
        if (r.Ok)
            return $"{name} liberado" + (r.ProcessesKilled > 0 ? $" ({r.ProcessesKilled} processo(s) finalizado(s))" : "") + ".";
        return "Falhou.";
    }
}
