using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace KitLugia.Core
{
    /// <summary>Correção aplicada que só entra em vigor depois de reiniciar.</summary>
    public sealed class PendingRepairEntry
    {
        public string Name { get; set; } = "";
        public string KeyPath { get; set; } = "";
        public string ValueName { get; set; } = "";
        public DateTime AppliedAtUtc { get; set; }
        /// <summary>Environment.TickCount no momento da aplicação (zera a cada boot).</summary>
        public int TickAtApply { get; set; }
        public string Reason { get; set; } = "";
    }

    /// <summary>
    /// Correção aplicada, PC reiniciou, e o check não pôde ser lido para dizer
    /// se o efeito teve lugar (NOT_FOUND/ERROR). Não é falha nem proteção:
    /// é "não deu para saber". Fica registrado para o usuário ter o rastro.
    /// </summary>
    public sealed class UnconfirmedCheck
    {
        public string Name { get; set; } = "";
        public string Reason { get; set; } = "";
        public DateTime WhenUtc { get; set; }
    }

    internal sealed class IntegrityState
    {
        public List<PendingRepairEntry> Pending { get; set; } = new();
        public List<UnconfirmedCheck> Unconfirmed { get; set; } = new();
    }

    /// <summary>
    /// Estado PENDING (amarelo) dos checks cuja correção só vale depois de
    /// reiniciar, e a INVALIDAÇÃO automática quando o efeito não vem.
    ///
    /// POR QUE EXISTE: o Guardian grava o valor no registro e o check voltava a
    /// ficar verde na hora. Mas em proteções como Integridade de Memória (HVCI),
    /// VBS e pagefile o Windows só aplica a configuração no boot seguinte. Um
    /// verde imediato é mentira; o certo é dizer "aguardando reinício" e depois
    /// conferir o que realmente aconteceu.
    ///
    /// FLUXO:
    ///   1. ToggleTweak grava o valor; se o check precisa de reinício, chama
    ///      MarkPending -> o item vira PENDING (amarelo) e fica fora do score.
    ///   2. Nos scans seguintes, sem reinício (TickCount crescendo), continua PENDING.
    ///   3. Depois de reiniciar, CheckTweak já deixou o status real do item:
    ///        OK        -> teve efeito, sai de PENDING;
    ///        MODIFIED  -> NÃO teve efeito: volta a MODIFIED (o estado real);
    ///        NOT_FOUND/ERROR -> não deu para confirmar; volta ao estado normal
    ///        (esses já eram o estado antes da correção, nada a inventar).
    /// </summary>
    public static class PendingRepairStore
    {
        private static readonly object Gate = new();
        private static IntegrityState? _cache;

        private static string FilePath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "KitLugia", "integrity-pending.json");

        /// <summary>Uptime máximo (ms) para aceitar um TickCount menor como boot novo.</summary>
        private const int FreshBootWindowMs = 24 * 60 * 60 * 1000;

        // ─────────────────────────── persistência ───────────────────────────
        private static IntegrityState Load()
        {
            if (_cache != null) return _cache;
            try
            {
                if (File.Exists(FilePath))
                {
                    var json = File.ReadAllText(FilePath);
                    _cache = JsonSerializer.Deserialize<IntegrityState>(json) ?? new IntegrityState();
                }
                else _cache = new IntegrityState();
            }
            catch { _cache = new IntegrityState(); }
            return _cache;
        }

        private static void Save(IntegrityState state)
        {
            try
            {
                var dir = Path.GetDirectoryName(FilePath);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                var tmp = FilePath + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(state,
                    new JsonSerializerOptions { WriteIndented = true }));
                // troca atômica: morrer no meio não deixa JSON pela metade
                File.Move(tmp, FilePath, true);
            }
            catch (Exception ex)
            {
                Logger.LogWarning("IntegrityState", "Nao foi possivel salvar: " + ex.Message);
            }
        }

        /// <summary>Quantos checks estão aguardando reinício.</summary>
        public static int PendingCount { get { lock (Gate) return Load().Pending.Count; } }

        // ─────────────────────────── marcação ───────────────────────────
        /// <summary>
        /// Registra que a correção foi aplicada mas só vale após reiniciar.
        /// </summary>
        public static void MarkPending(ScannableTweak tweak, string motivo)
        {
            if (tweak == null) return;
            lock (Gate)
            {
                var s = Load();

                // já estava pendente? atualiza, não duplica
                string chave = tweak.StoreKey;
                s.Pending.RemoveAll(p => p.Name == chave);
                s.Unconfirmed.RemoveAll(u => u.Name == chave);

                s.Pending.Add(new PendingRepairEntry
                {
                    Name = chave,
                    KeyPath = tweak.KeyPath ?? "",
                    ValueName = tweak.ValueName ?? "",
                    AppliedAtUtc = DateTime.UtcNow,
                    TickAtApply = Environment.TickCount,
                    Reason = motivo
                });

                Save(s);
                Logger.Log($"[INTEGRIDADE] '{tweak.Name}' corrigido mas aguarda reinicio: {motivo}");
            }
        }

        // ─────────────────────────── avaliação no scan ───────────────────────────
        /// <summary>
        /// Aplica PENDING/NÃO-CONFIRMADO sobre a lista JÁ LIDA por CheckTweak.
        /// Precisa ser chamada no fim de GetHarmfulTweaksWithStatus.
        /// </summary>
        public static void Apply(IReadOnlyList<ScannableTweak> tweaks)
        {
            if (tweaks == null || tweaks.Count == 0) return;
            bool mudou = false;

            lock (Gate)
            {
                var s = Load();
                if (s.Pending.Count == 0 && s.Unconfirmed.Count == 0) return;

                var porNome = new Dictionary<string, ScannableTweak>(StringComparer.OrdinalIgnoreCase);
                foreach (var t in tweaks)
                {
                    // Chave estavel (Id). O Name tambem e indexado para
                    // reencontrar entrada gravada por versao anterior,
                    // quando ainda nao existia Id.
                    //
                    // O Id pode ser COMPARTILHADO (checks diferentes no mesmo
                    // alvo; o PATH tem 6). O dictionary mantem o ULTIMO
                    // nesse caso — aceitavel porque so um check por grupo
                    // costuma precisar de reinicio, e a combinacao
                    // (Id + HarmfulValue) e' o que distingue a intencao.
                    porNome[t.StoreKey] = t;
                    if (!porNome.ContainsKey(t.Name)) porNome[t.Name] = t;
                }

                // 1) não confirmados: o check voltou ao fluxo normal, mas o
                // fato de que a correção não pôde ser verificada fica visível.
                foreach (var u in s.Unconfirmed)
                {
                    if (!porNome.TryGetValue(u.Name, out var tw)) continue;
                    tw.UnconfirmedReason = u.Reason;
                }

                // 2) pendentes
                int agora = Environment.TickCount;
                var restantes = new List<PendingRepairEntry>();

                foreach (var p in s.Pending)
                {
                    // check não existe mais na lista: descartar o marcador
                    if (!porNome.TryGetValue(p.Name, out var tw)) { mudou = true; continue; }

                    // Environment.TickCount zera a cada boot. Menor que o gravado +
                    // uptime curto = reiniciou de verdade (o segundo teste evita
                    // o falso positivo do wraparound de ~24,9 dias).
                    bool reiniciou = agora < p.TickAtApply && agora < FreshBootWindowMs;

                    if (!reiniciou)
                    {
                        tw.PendingReason = p.Reason;
                        tw.Status = TweakStatus.PENDING;
                        restantes.Add(p);
                        continue;
                    }

                    // Reiniciou: o status atual vem do CheckTweak real (registro,
                    // serviço, bcdedit...), não de comparação de string.
                    switch (tw.Status)
                    {
                        case TweakStatus.OK:
                            Logger.Log($"[INTEGRIDADE] '{p.Name}' teve efeito apos o reinicio: agora esta protegido.");
                            tw.PendingReason = null;
                            mudou = true;
                            continue;

                        case TweakStatus.MODIFIED:
                            // A correcao foi aplicada, o Windows reiniciou e o
                            // valor continua no estado inseguro.
                            //
                            // ANTES isto virava "INVALIDADO": saia do score e
                            // mostrava um selo laranja fixo. Na pratica era uma
                            // mentira permanente - CFG/DEP/SMBv1/Spectre/UAC
                            // ficaram presos assim mesmo estando protegidos (o
                            // Core lia OK) e o score dizia 100% ao lado de
                            // cinco bandeiras de "invalidado".
                            //
                            // Agora o item fica no que REALMENTE esta:
                            // MODIFIED. E a verdade, o utilizador ve que a
                            // protecao continua desligada, e o "Restaurar
                            // Todos" continua a ter o que corrigir.
                            Logger.Log($"[INTEGRIDADE] '{p.Name}': a correcao foi aplicada, "
                                       + $"o Windows reiniciou e o valor continua inseguro. "
                                       + $"O item volta ao estado normal (MODIFIED).");
                            tw.PendingReason = null;
                            mudou = true;
                            continue;

default:
                            // NOT_FOUND / ERROR: o check nao pode ser lido nesta
                            // maquina, entao nao da para dizer se a correcao
                            // teve efeito. Antes isso sumia sem deixar rastro
                            // (so o log), e o usuario ficava sem nenhuma pista
                            // do que aconteceu. Agora fica registrado como
                            // "nao confirmado" e volta ao fluxo normal SEM
                            // inventar protecao nem invalidacao.
                            string motivoNaoConfirmado =
                                $"A correção foi aplicada e o Windows reiniciou, mas o check não " +
                                $"pôde ser lido aqui (estado {tw.Status}) — não dá para afirmar se " +
                                $"o efeito teve lugar. O item voltou ao fluxo normal sem contar como " +
                                $"protegido.";
                            Logger.Log($"[INTEGRIDADE] '{p.Name}' NÃO CONFIRMADO: {motivoNaoConfirmado}");
                            s.Unconfirmed.RemoveAll(u => u.Name == p.Name);
                            s.Unconfirmed.Add(new UnconfirmedCheck
                            {
                                Name = p.Name,
                                Reason = motivoNaoConfirmado,
                                WhenUtc = DateTime.UtcNow
                            });
                            tw.PendingReason = null;
                            tw.UnconfirmedReason = motivoNaoConfirmado;
                            mudou = true;
                            continue;
                    }
                }

                if (restantes.Count != s.Pending.Count)
                {
                    s.Pending = restantes;
                    mudou = true;
                }
                if (mudou) Save(s);
            }
        }

        /// <summary>
        /// Este check só entra em vigor depois de reiniciar?
        /// true = explícito no item, ou detectado pelo caminho/nome (features que o
        /// Windows só aplica no boot seguinte).
        /// </summary>
        public static bool NeedsRebootAfterFix(ScannableTweak tweak)
        {
            if (tweak == null) return false;
            if (tweak.RequiresReboot) return true;

            string nome = tweak.Name ?? "";
            string chave = tweak.KeyPath ?? "";

            // DeviceGuard (HVCI / VBS): o hypervisor só carrega a config no boot
            if (chave.Contains("DeviceGuard", StringComparison.OrdinalIgnoreCase))
                return true;

            // pagefile / memória gerenciada.
            // SEM NORMALIZACAO o "Arquivo de Paginacao" (sem acento, como esta
            // escrito no codigo) nao casaria com "Paginação" e o item ficaria
            // VERDE na hora - a mesma mentira que o PENDING existe para evitar.
            string norm = nome.Replace("í", "i").Replace("í", "i")
                              .Replace("ã", "a").Replace("á", "a").Replace("â", "a")
                              .Replace("é", "e").Replace("ê", "e").Replace("ó", "o")
                              .Replace("ô", "o").Replace("ç", "c").Replace("ú", "u");
            if (norm.Contains("Pagefile", StringComparison.OrdinalIgnoreCase) ||
                norm.Contains("Arquivo de Paginacao", StringComparison.OrdinalIgnoreCase) ||
                norm.Contains("Paging", StringComparison.OrdinalIgnoreCase))
                return true;

            return false;
        }
    }
}