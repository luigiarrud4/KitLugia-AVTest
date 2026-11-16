using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Threading.Tasks;

namespace KitLugia.Bootstrap
{
    /// <summary>Passo de progresso do download/instalacao.</summary>
    public sealed class InstallProgress
    {
        public double Percent { get; set; }
        public string Status { get; set; } = "";
        public bool Indeterminate { get; set; }
    }

    /// <summary>
    /// Resultado da instalacao de um componente.
    /// Espelha os estados que o LuaTools trata (Velopack Runtimes API):
    /// sucesso / cancelado pelo usuario / instalado mas o Windows precisa reiniciar.
    /// </summary>
    public enum InstallOutcome
    {
        /// <summary>Instalado com sucesso (codigo 0).</summary>
        Success,

        /// <summary>O usuario cancelou a confirmacao de administrador (codigo 1223).</summary>
        UserCancelled,

        /// <summary>Instalado, mas o Windows precisa reiniciar (codigo 3010).</summary>
        RestartRequired,

        /// <summary>Falhou (qualquer outro codigo / excecao).</summary>
        Failed
    }

    /// <summary>
    /// Baixa e instala os componentes oficiais da Microsoft.
    ///
    /// O download vai para %TEMP% (o instalador oficial e' um .exe assinado que
    /// pede o UAC sozinho — por isso o launcher roda como usuario e nao como admin).
    /// Nenhum arquivo de fora do servidor oficial e' baixado: as URLs sao
    /// aka.ms (redirecionador oficial da Microsoft) / builds.dotnet.microsoft.com.
    /// </summary>
    public static class ComponentInstaller
    {
        /// <summary>
        /// Baixa o instalador do componente. Tenta cada URL em ordem e valida o
        /// resultado (tamanho minimo + cabecalho MZ) antes de aceitar.
        /// </summary>
        public static async Task<bool> DownloadAsync(RequiredComponent component,
            IProgress<InstallProgress> progress, Action<string> log)
        {
            string target = Path.Combine(Path.GetTempPath(), component.FileName);

            foreach (string url in component.Urls)
            {
                try
                {
                    log?.Invoke("[Bootstrap] Baixando " + url + " ...");

                    var request = (HttpWebRequest)WebRequest.Create(url);
                    request.UserAgent = "KitLugia/2.0 (bootstrapper)";
                    request.Timeout = 30000;              // conexao
                    request.ReadWriteTimeout = 120000;    // leitura
                    request.AutomaticDecompression = DecompressionMethods.None;

                    using (var response = (HttpWebResponse)await request.GetResponseAsync().ConfigureAwait(false))
                    {
                        long total = response.ContentLength; // -1 = desconhecido
                        using (Stream input = response.GetResponseStream())
                        using (var output = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None))
                        {
                            byte[] buffer = new byte[192 * 1024];
                            long received = 0;
                            int read;

                            progress?.Report(new InstallProgress
                            {
                                Percent = 0,
                                Status = "Baixando " + component.DisplayName + "...",
                                Indeterminate = total <= 0
                            });

                            while ((read = await input.ReadAsync(buffer, 0, buffer.Length).ConfigureAwait(false)) > 0)
                            {
                                await output.WriteAsync(buffer, 0, read).ConfigureAwait(false);
                                received += read;

                                double pct = total > 0 ? (received * 100.0 / total) : 0;
                                progress?.Report(new InstallProgress
                                {
                                    Percent = Math.Min(100.0, pct),
                                    Status = "Baixando " + component.DisplayName + "...",
                                    Indeterminate = total <= 0
                                });
                            }
                        }
                    }

                    if (IsValidInstaller(target))
                    {
                        long mb = new FileInfo(target).Length / 1024 / 1024;
                        log?.Invoke("[Bootstrap] Download OK: " + target + " (" + mb + " MB)");
                        return true;
                    }

                    log?.Invoke("[Bootstrap] Arquivo baixado invalido, tentando proxima URL: " + url);
                    TryDelete(target);
                }
                catch (Exception ex)
                {
                    log?.Invoke("[Bootstrap] Falha ao baixar " + url + ": " + ex.Message);
                    TryDelete(target);
                }
            }

            return false;
        }

        /// <summary>
        /// Executa o instalador oficial em modo silencioso. O proprio instalador
        /// tem o manifesto "highestAvailable" e dispara o UAC.
        /// O codigo de saida e' mapeado nos 3 estados que importam:
        /// 0 = ok, 3010 = ok mas precisa reiniciar, 1223 = o usuario cancelou o UAC.
        /// </summary>
        public static InstallOutcome Install(RequiredComponent component, Action<string> log)
        {
            string target = Path.Combine(Path.GetTempPath(), component.FileName);
            if (!File.Exists(target))
            {
                log?.Invoke("[Bootstrap] Instalador ausente em " + target);
                return InstallOutcome.Failed;
            }

            try
            {
                log?.Invoke("[Bootstrap] Executando instalador: " + component.FileName + " " + component.InstallArgs);
                var psi = new ProcessStartInfo(target, component.InstallArgs)
                {
                    UseShellExecute = true,     // deixa o instalador pedir o UAC
                    WindowStyle = ProcessWindowStyle.Hidden
                };
                using (var process = Process.Start(psi))
                {
                    if (process == null)
                    {
                        log?.Invoke("[Bootstrap] Process.Start retornou null.");
                        return InstallOutcome.Failed;
                    }
                    process.WaitForExit();
                    int code = process.ExitCode;
                    log?.Invoke("[Bootstrap] Instalador terminou com codigo " + code);

                    if (code == 0) return InstallOutcome.Success;
                    if (code == 3010) return InstallOutcome.RestartRequired;   // precisa reiniciar
                    if (code == 1223) return InstallOutcome.UserCancelled;    // UAC cancelado
                    return InstallOutcome.Failed;
                }
            }
            catch (Exception ex)
            {
                log?.Invoke("[Bootstrap] Erro ao executar o instalador: " + ex.Message);
                return InstallOutcome.Failed;
            }
        }

        /// <summary>Valida o arquivo baixado: existe, tem tamanho e comeca com MZ (PE).</summary>
        private static bool IsValidInstaller(string path)
        {
            try
            {
                if (!File.Exists(path)) return false;
                var info = new FileInfo(path);
                if (info.Length < 500 * 1024) return false;   // instalador real e' MB

                using (var fs = File.OpenRead(path))
                {
                    int b0 = fs.ReadByte();
                    int b1 = fs.ReadByte();
                    return b0 == 'M' && b1 == 'Z';
                }
            }
            catch { return false; }
        }

        private static void TryDelete(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { }
        }
    }
}
