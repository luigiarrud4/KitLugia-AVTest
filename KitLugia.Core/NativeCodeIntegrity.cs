using System;
using System.Runtime.InteropServices;

namespace KitLugia.Core
{
    /// <summary>
    /// Estado REAL do Code Integrity / HVCI em runtime, via
    /// NtQuerySystemInformation(SystemCodeIntegrityInformation = 103).
    ///
    /// POR QUE EXISTE: o check de HVCI lia
    /// HKLM\SYSTEM\CurrentControlSet\Control\DeviceGuard\Scenarios\
    /// HypervisorEnforcedCodeIntegrity\Enabled — um DWORD que QUALQUER programa
    /// pode escrever. Ou seja: ele respondia "a configuração pedida", não "o que
    /// está realmente aplicado". Escrever 1 nele e o check ficar verde não prova
    /// nada: o hypervisor só carrega isso no próximo boot, e pode recusar se a
    /// CPU/firmware não suportarem.
    ///
    /// A classe 103 devolve o bitmask que o KERNEL está aplicando agora:
    ///   0x00000400  HVCI_KMCI_ENABLED             -> HVCI impondo
    ///   0x00000800  HVCI_KMCI_AUDITMODE_ENABLED   -> HVCI só em auditoria ( NÃO impõe )
    ///   0x00001000  HVCI_KMCI_STRICTMODE_ENABLED
    ///   0x00002000  HVCI_IUM_ENABLED
    /// Fonte: MS Learn (NtQuerySystemInformation), geoffchappell.com, ntdoc.
    ///
    /// QUANDO A API NÃO RESPONDE: devolve Available=false e o chamador cai no
    /// registro — mas com o texto dizendo que é a CONFIGURAÇÃO PEDIDA, não o
    /// estado real. Honestidade em vez de número inventado.
    /// </summary>
    public static class NativeCodeIntegrity
    {
        [DllImport("kernel32.dll", EntryPoint = "GetModuleHandleW", ExactSpelling = true, SetLastError = true)]
        private static extern IntPtr GetModuleHandle(
            [MarshalAs(UnmanagedType.LPWStr)] string lpModuleName);

        // QUIRK (medido pelo probe, nao por suposicao — a primeira tentativa
        // falhou e a hipotese inicial estava errada):
        //  - GetModuleHandleW e' funcao W: o parametro PRECISA de LPWStr. Sem o
        //    MarshalAs, o CLR marshala a string como ANSI, a API W le bytes
        //    invalidos e devolve NULL em silencio. Sintoma: "ntdll nao encontrada".
        //  - GetProcAddress e' ANSI-only: LPStr + ExactSpelling=true evita o CLR
        //    procurar a variante "W" (que nao existe em kernel32).
        // Isolado em %TEMP%/opencode/isol: as duas formas com o marshaling certo
        // devolvem o mesmo ponteiro.
        [DllImport("kernel32.dll", EntryPoint = "GetProcAddress",
            ExactSpelling = true, SetLastError = true)]
        private static extern IntPtr GetProcAddress(IntPtr hModule,
            [MarshalAs(UnmanagedType.LPStr)] string lpProcName);

        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        private delegate int NtQuerySystemInformationDelegate(
            int SystemInformationClass,
            IntPtr SystemInformation,
            int SystemInformationLength,
            IntPtr ReturnLength);

        private const int SystemCodeIntegrityInformation = 103;

        // Bitmask do CodeIntegrityOptions (segundo MS Learn).
        public const uint OPTION_ENABLED = 0x00000001;
        public const uint OPTION_UMCI_ENABLED = 0x00000004;
        public const uint OPTION_HVCI_KMCI_ENABLED = 0x00000400;
        public const uint OPTION_HVCI_KMCI_AUDITMODE = 0x00000800;
        public const uint OPTION_HVCI_KMCI_STRICTMODE = 0x00001000;
        public const uint OPTION_HVCI_IUM_ENABLED = 0x00002000;

        private static NtQuerySystemInformationDelegate? _resolve;
        private static bool _resolveTentado;

        /// <summary>
        /// Carrega NtQuerySystemInformation por GetProcAddress.
        ///
        /// QUIRK IMPORTANTE (aprendido neste projeto): GetProcAddress é
        /// ANSI-ONLY. Declarar o DllImport com CharSet.Unicode devolve 0
        /// silenciosamente e a função simplesmente nunca funciona — sem erro,
        /// sem exceção, só o resultado vazio. Por isso é CharSet.Ansi.
        /// </summary>
        private static NtQuerySystemInformationDelegate? GetApi()
        {
            if (_resolveTentado) return _resolve;
            _resolveTentado = true;
            try
            {
                var h = GetModuleHandle("ntdll.dll");
                if (h == IntPtr.Zero) return null;
                var p = GetProcAddress(h, "NtQuerySystemInformation");
                if (p == IntPtr.Zero) return null;
                _resolve = Marshal.GetDelegateForFunctionPointer<NtQuerySystemInformationDelegate>(p);
            }
            catch { _resolve = null; }
            return _resolve;
        }

        /// <summary>
        /// Lê o estado de Code Integrity em runtime.
        /// </summary>
        public static bool TryGetState(out uint codeIntegrityOptions, out string motivo)
        {
            codeIntegrityOptions = 0;
            motivo = "";

            var api = GetApi();
            if (api == null)
            {
                motivo = "NtQuerySystemInformation(NtQuerySystemInformation=103) não foi encontrado em ntdll.dll";
                return false;
            }

            // A doc diz que Length deve ser preenchido pelo chamador e a
            // estrutura tem ULONG Length + ULONG CodeIntegrityOptions (8 B),
            // mas o kernel pode exigir um buffer maior: aloca folgado e usa o
            // loop de tamanho em vez de assumir 8.
            //
            // MEDIDO: com IntPtr.Zero no ReturnLength o kernel responde
            // STATUS_INFO_LENGTH_MISMATCH (0xC0000004) e NAO diz o tamanho que
            // quer. Passando um ponteiro de saida, o 103 devolve o tamanho exato
            // (medido: 12 B nesta maquina) e a segunda chamada preenche o
            // buffer. Sem isso a leitura dava "indisponivel" em toda maquina.
            int tamanho = 0;
            var sizeOut = Marshal.AllocHGlobal(sizeof(int));
            try
            {
                Marshal.WriteInt32(sizeOut, 0, 0);

                // 1a passada: descobrir o tamanho que o kernel exige
                var probe = Marshal.AllocHGlobal(16);
                try
                {
                    Marshal.WriteInt32(probe, 0, 16);
                    api(SystemCodeIntegrityInformation, probe, 16, sizeOut);
                    tamanho = Marshal.ReadInt32(sizeOut, 0);
                }
                finally { Marshal.FreeHGlobal(probe); }

                if (tamanho < 8) tamanho = 8;
                if (tamanho > 4096) tamanho = 4096;   // defensivo: valor absurdo = bug

                // 2a passada: ler de verdade
                var buf = Marshal.AllocHGlobal(tamanho);
                try
                {
                    Marshal.WriteInt32(buf, 0, tamanho);   // Length = sizeof(ULONG)

                    int status = api(SystemCodeIntegrityInformation, buf, tamanho, sizeOut);

                    if (status == 0)
                    {
                        int length = Marshal.ReadInt32(buf, 0);
                        if (length < 8 || length > tamanho)
                        {
                            motivo = $"resposta com Length inesperado ({length}) para um buffer de {tamanho} B";
                            return false;
                        }
                        codeIntegrityOptions = unchecked((uint)Marshal.ReadInt32(buf, 4));
                        return true;
                    }

                    if (status == unchecked((int)0xC0000004))
                    {
                        motivo = "o kernel exige um buffer maior do que o informado " +
                                 $"({tamanho} B) e nao informa o tamanho necessario";
                        return false;
                    }

                    motivo = $"NtQuerySystemInformation retornou NTSTATUS 0x{status:X8}";
                    return false;
                }
                finally { Marshal.FreeHGlobal(buf); }
            }
            finally { Marshal.FreeHGlobal(sizeOut); }
        }

        /// <summary>
        /// HVCI está IMPONDO agora? (não em audit mode, que só registra eventos)
        /// </summary>
        public static bool TryGetHvci(out bool impoe, out bool auditMode, out bool disponivel, out uint opcoes, out string motivo)
        {
            impoe = false; auditMode = false; disponivel = false; opcoes = 0; motivo = "";
            if (!TryGetState(out opcoes, out motivo)) return false;
            disponivel = true;
            auditMode = (opcoes & OPTION_HVCI_KMCI_AUDITMODE) != 0;
            impoe = (opcoes & OPTION_HVCI_KMCI_ENABLED) != 0;
            return true;
        }

        /// <summary>Resumo curto para log/diagnóstico.</summary>
        public static string Descrever()
        {
            if (!TryGetState(out uint o, out string m))
                return "indisponível (" + m + ")";
            return $"0x{o:X8}"
                 + $" [KernelCI={(o & OPTION_ENABLED) != 0}"
                 + $", UMCI={(o & OPTION_UMCI_ENABLED) != 0}"
                 + $", HVCI={(o & OPTION_HVCI_KMCI_ENABLED) != 0}"
                 + $", Audit={(o & OPTION_HVCI_KMCI_AUDITMODE) != 0}"
                 + $", Strict={(o & OPTION_HVCI_KMCI_STRICTMODE) != 0}"
                 + $", IUM={(o & OPTION_HVCI_IUM_ENABLED) != 0}]";
        }
    }
}