using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace KitLugia.Core
{
    /// <summary>
    /// Leitura da loja BCD pelo REGISTRY, sem depender do binário bcdedit.exe e sem
    /// depender do idioma do Windows.
    ///
    /// CONTEXTO (docs/KITLUGIA_PREOS_GAPS.md §4 / docs/EASEUS_EPM_DESKTOP.md §2.6):
    /// O EaseUS NÃO usa "bcdedit /enum all" para verificar se uma entrada existe. Ele abre
    /// HKLM\BCD00000000\Objects\{guid}\Elements\{id}\Element — que é a projeção em registry da
    /// loja BCD ativa — e lê o blob direto (achado em CPreOsPEBcdProc, sub_1800D9A30 no
    /// CloneModule.dll). Isso resolve dois problemas que já custaram tempo no KitLugia:
    ///
    ///   1. LOCALIZACAO: o bcdedit imprime "identifier"/"description" em inglês e
    ///      "Identificador"/"Descricao" em pt-BR. O parsing multilíngue de 02/08 removia
    ///      0 entradas silenciosamente quando o sistema estava em português.
    ///   2. DEPENDENCIA: o binário bcdedit.exe precisa existir no PATH/System32 do host.
    ///
    /// Esta classe é READ-ONLY. Nada aqui escreve na BCD — escrita continua via bcdedit,
    /// que é testado e funciona. O ganho é unicamente na LEITURA/verificação.
    /// </summary>
    public static class BcdRegistry
    {
        /// <summary>Raiz da projeção em registry da loja BCD ativa.</summary>
        private const string BcdRoot = @"BCD00000000\Objects";

        /// <summary>
        /// Chave do GUID do ramdisk de boot (template do Windows). Usada pelo EaseUS para
        /// descobrir quais entradas referenciam um ramdisk. Mantida aqui para futures
        /// verificações (ex.: detectar PE pendente deixado por outra ferramenta).
        /// </summary>
        public const string RamdiskOptionsTemplateGuid = "{9dea862c-5cdd-4e70-acc1-f32b344d4795}";

        /// <summary>
        /// Indica se a projeção em registry está disponível neste host.
        /// Se false, o chamador DEVE usar o bcdedit como fallback — não significa
        /// "não há entradas", significa "não consigo ver".
        /// </summary>
        public static bool IsAvailable()
        {
            try
            {
                using var root = Registry.LocalMachine.OpenSubKey(BcdRoot);
                return root != null && root.GetSubKeyNames().Length > 0;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Versão assíncrona de <see cref="IsAvailable"/> (mantida a paridade com o resto do Core,
        /// que é todo async, para não bloquear a thread de UI durante o enum).
        /// </summary>
        public static Task<bool> IsAvailableAsync() => Task.Run(IsAvailable);

        /// <summary>
        /// Retorna os GUIDs dos objetos da BCD cujo conteúdo contém TODAS as
        /// <paramref name="mustContain"/> strings.
        ///
        /// VARRE TODOS os elementos de TODOS os objetos e compara o texto, seja ele
        /// REG_SZ/REG_MULTI_SZ (string) ou REG_BINARY decodificado como UTF-16. Isso é
        /// proposital: não é preciso conhecer o ID de cada elemento, então a busca não quebra
        /// se a Microsoft mudar o schema. (Ainda assim, ver <see cref="DescriptionElementId"/>.)
        ///
        /// FORMATO REAL (medido em 03/10/2026 neste host, HKLM\BCD00000000\Objects):
        ///   12000004  REG_SZ  DESCRIÇÃO   ("Windows Recovery Environment", "UEFI:CD/DVD Drive")
        ///   12000002  REG_SZ  path       (\windows\system32\winload.efi)
        ///   12000005  REG_SZ  locale     (pt-BR)
        ///   22000002  REG_SZ  systemroot  (\windows)
        ///   32000004  REG_SZ  boot.sdi   (\Recovery\WindowsRE\boot.sdi)
        ///   11000001  REG_BINARY device  (88 bytes = só volume, 200 bytes = volume+partição)
        ///   14000006  REG_MULTI_SZ inherit
        /// IMPORTANTE: a descrição NÃO é binária — é REG_SZ. Uma implementação que só
        /// procure texto em byte[] não acha NADA (erro cometido e corrigido em 03/10/2026).
        ///
        /// Semântica equivalente ao antigo FindBcdGuidsByText (bcdedit): devolve o objeto cujo
        /// texto casa. Como o EaseUS grava a MESMA descrição no objeto-device e no objeto-ramdisk
        /// (ver _createEntryInBcd), os dois GUIDs são devolvidos — e os dois precisam ser
        /// removidos no cleanup, exatamente como o DelBcdEntry do EaseUS faz.
        /// </summary>
        public static List<string> FindGuidsByText(params string[] mustContain)
        {
            var result = new List<string>();
            if (mustContain == null || mustContain.Length == 0) return result;

            try
            {
                using var root = Registry.LocalMachine.OpenSubKey(BcdRoot);
                if (root == null) return result;

                foreach (var guidName in root.GetSubKeyNames())
                {
                    // Subchaves são os objetos da BCD: "{9dea862c-...}". Alguns builds
                    // projetam sem as chaves; normalizamos para o formato canônico.
                    string canonical = NormalizeGuid(guidName);
                    if (canonical == null) continue;

                    if (ObjectContainsText(root, guidName, mustContain))
                        result.Add(canonical);
                }
            }
            catch (Exception ex)
            {
                // Nunca deixar a leitura estourar: quem chama deve cair no bcdedit.
                Logger.LogWarning("BcdRegistry", $"Falha ao ler BCD do registry: {ex.Message}");
            }

            return result;
        }

        /// <summary>
        /// Versão assíncrona de <see cref="FindGuidsByText"/>.
        /// </summary>
        public static Task<List<string>> FindGuidsByTextAsync(params string[] mustContain)
            => Task.Run(() => FindGuidsByText(mustContain));

        /// <summary>
        /// ID do elemento que guarda a DESCRIÇÃO de um objeto da BCD (medido em 03/10/2026).
        /// Usado só para mensagens de log/diagnóstico — a busca em FindGuidsByText varre
        /// todos os elementos de propósito, para não depender deste número.
        /// </summary>
        public const string DescriptionElementId = "12000004";

        /// <summary>
        /// Retorna a descrição (elemento 12000004) de um objeto da BCD, ou null.
        /// </summary>
        public static string? GetDescription(string guid)
        {
            if (string.IsNullOrWhiteSpace(guid)) return null;
            foreach (var candidate in new[] { guid.Trim(), guid.Trim().Trim('{', '}') })
            {
                try
                {
                    using var k = Registry.LocalMachine.OpenSubKey(
                        $@"{BcdRoot}\{candidate}\Elements\{DescriptionElementId}");
                    if (k?.GetValue("Element") is string s && !string.IsNullOrWhiteSpace(s))
                        return s;
                }
                catch { /* tenta a próxima variante */ }
            }
            return null;
        }

        /// <summary>
        /// Verifica se um GUID específico existe na BCD. Barato (1 RegOpenKeyEx) e
        /// substitui o parse de "bcdedit /enum all" só para checar idempotência —
        /// o equivalente ao IsAlreadyDone do EaseUS (CEUPreOsMgr::IsAlreadyDone).
        /// </summary>
        public static bool EntryExists(string guid)
        {
            if (string.IsNullOrWhiteSpace(guid)) return false;
            string name = guid.Trim();

            // Tenta com e sem aspas — o registry aceita as duas formas como nome de subchave.
            foreach (var candidate in new[] { name, name.Trim('{', '}') })
            {
                try
                {
                    using var k = Registry.LocalMachine.OpenSubKey($@"{BcdRoot}\{candidate}");
                    if (k != null) return true;
                }
                catch
                {
                    // ignora e tenta a próxima variante
                }
            }
            return false;
        }

        /// <summary>Versão assíncrona de <see cref="EntryExists"/>.</summary>
        public static Task<bool> EntryExistsAsync(string guid) => Task.Run(() => EntryExists(guid));

        // ---------------------------------------------------------------------------------------

        /// <summary>
        /// Procura, em todos os elementos de um objeto da BCD, o valor que contenha os textos.
        /// </summary>
        private static bool ObjectContainsText(RegistryKey root, string guidName, string[] mustContain)
        {
            try
            {
                using var obj = root.OpenSubKey(guidName + @"\Elements");
                if (obj == null) return false;

                foreach (var elementId in obj.GetSubKeyNames())
                {
                    string? text;
                    using (var el = obj.OpenSubKey(elementId))
                    {
                        if (el == null) continue;
                        text = ElementToText(el.GetValue("Element"));
                    }
                    if (string.IsNullOrEmpty(text)) continue;

                    if (mustContain.All(k => text.Contains(k, StringComparison.OrdinalIgnoreCase)))
                        return true;
                }
            }
            catch
            {
                // objeto inacessível: pula para o próximo
            }
            return false;
        }

        /// <summary>
        /// Converte o valor de um elemento BCD em texto pesquisável.
        /// Cobre os formatos reais medidos em 03/10/2026:
        ///   REG_SZ / REG_EXPAND_SZ  -> string direta (é o caso da DESCRIÇÃO 12000004)
        ///   REG_MULTI_SZ            -> lista de strings
        ///   REG_BINARY              -> UTF-16 (alguns campos legíveis moram em blob binário)
        /// </summary>
        private static string? ElementToText(object? raw)
        {
            switch (raw)
            {
                case string s:
                    return s;

                case string[] arr:
                    return string.Join(" ", arr);

                case byte[] blob:
                    return DecodeUtf16(blob);

                default:
                    return null;
            }
        }

        /// <summary>
        /// Extrai o texto legível de um elemento BCD binário. Só processa blobs com cara de
        /// string (UTF-16 tem NUL em pares); um "replace" de NUL basta porque só precisamos
        /// saber se o texto está presente, não o valor exato do campo.
        /// </summary>
        private static string? DecodeUtf16(byte[] blob)
        {
            if (blob.Length == 0) return null;

            int zeroes = 0;
            foreach (byte b in blob) { if (b == 0) zeroes++; }
            if (zeroes * 4 < blob.Length) return null; // predominanteemente não-UTF16

            return Encoding.Unicode.GetString(blob).Replace("\0", string.Empty);
        }

        /// <summary>Normaliza um nome de subchave para o formato {guid} canônico.</summary>
        private static string? NormalizeGuid(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return null;
            string s = name.Trim().Trim('{', '}');
            return Guid.TryParse(s, out var g) ? "{" + g.ToString().ToLowerInvariant() + "}" : null;
        }
    }
}
