using System;
using System.Net;
using System.Net.Http;

namespace KitLugia.Core
{
    /// <summary>
    /// HttpClient central do Core. O padrao antigo ("using var c = new HttpClient()") esvazia o
    /// pool de sockets sob uso repetido (TIME_WAIT/sockets em CLOSE_WAIT) porque o HttpClient
    /// dispoeh o handler junto — inclusive durante downloads em progresso. Aqui o handler e
    /// compartilhado e as conexoes ociosas sao recicladas periodicamente (PooledConnectionLifetime),
    /// sem custo de DNS obsoleto.
    ///
    /// Uso:
    ///   - Requisicoes simples:            KitHttp.Shared.GetStringAsync(url, ct)
    ///   - Com timeout por chamada:        using var c = KitHttp.CreateClient(TimeSpan.FromSeconds(10));
    ///     (o using dispoeh apenas o wrapper — o handler permanece vivo)
    /// </summary>
    public static class KitHttp
    {
        private static readonly HttpMessageHandler SharedHandler = new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(2),
            PooledConnectionIdleTimeout = TimeSpan.FromSeconds(30),
            AutomaticDecompression = DecompressionMethods.All,
        };

        /// <summary>Client compartilhado sem timeout por requisicao — controle via CancellationToken.</summary>
        public static readonly HttpClient Shared = new(SharedHandler, disposeHandler: false);

        /// <summary>
        /// Client leve que HERDA o handler compartilhado e define apenas o timeout por requisicao.
        /// Pode ser usado com using — o handler nao e dispoado.
        /// </summary>
        public static HttpClient CreateClient(TimeSpan requestTimeout) =>
            new(SharedHandler, disposeHandler: false) { Timeout = requestTimeout };
    }
}
