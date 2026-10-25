using System;
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace KitLugia.Core
{
    /// <summary>
    /// Lixeira pelo shell (SHFileOperationW), com as mesmas flags do Revo.
    ///
    /// POR QUE TROCAR A IMPLEMENTAÇÃO (medido, não suposto):
    /// o código usava Microsoft.VisualBasic.FileSystem com UIOption.OnlyErrorDialogs.
    /// num harness comparando as duas vias, as três diferenças que importam para
    /// "desinstalar com segurança" são:
    ///
    ///  1. Cancelamento invisível. Com OnlyErrorDialogs, se o usuário disser
    ///     "Não" na confirmação, a operação aborta — mas o código a trata como
    ///     sucesso e marca FilesDeleted++. O arquivo continua lá e o relatório
    ///     diz que foi removido. O Revo checa `fAnyOperationsAborted`.
    ///  2. Fallback silencioso para definitivo. No catch, o código fazia Directory.Delete
    ///     (apaga DE VERDADE) e ainda logava "[permanent]" como se fosse uma
    ///     escolha do usuário. Não era: era falha da lixeira. O usuário pedia
    ///     "pode apagar?" e recebia um sumiço irreversível sem ter sido avisado.
    ///  3. UI durante a remoção em lote. OnlyErrorDialogs pode abrir diálogo no
    ///     meio de uma fila de dezenas de itens.
    ///
    /// O Revo (docs/REVO_UNINSTALLER_ANALYSIS.md, sub_14003BA80) usa
    /// SHFileOperationW com fFlags = 0x414 quando DelToBin=1 e 0x454 quando
    /// =0 — as mesmas flags aqui.
    /// </summary>
    public static class RecycleBinOp
    {
        private const uint FO_DELETE = 3;

        // Flags reais (shellapi.h)
        private const ushort FOF_RENAMEONCOLLISION = 0x0008;
        private const ushort FOF_ALLOWUNDO = 0x0040;
        private const ushort FOF_NOCONFIRMATION = 0x0010;
        private const ushort FOF_NOCONFIRMMKDIR = 0x0200;
        private const ushort FOF_NOERRORUI = 0x0400;
        private const ushort FOF_NODIRFLAGS = 0x0020;

        // Layout NATIVO x64. NUNCA usar string gerenciada com [MarshalAs(LPWStr)]
        // aqui: o marshaler usa um ponteiro temporário para a cópia gerenciada e
        // o shell o lê DEPOIS que ela já foi liberada -> 0xC0000005. Aloca manual.
        [StructLayout(LayoutKind.Sequential)]
        private struct SHFILEOPSTRUCT
        {
            public IntPtr hwnd;
            public uint wFunc;
            public IntPtr pFrom;
            public IntPtr pTo;
            public ushort fFlags;
            [MarshalAs(UnmanagedType.Bool)] public bool fAnyOperationsAborted;
            public IntPtr hNameMappings;
            public IntPtr lpszProgressTitle;
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern int SHFileOperation(ref SHFILEOPSTRUCT op);

        /// <summary>Resultado de uma operação de lixeira.</summary>
        public enum RecycleResult
        {
            /// <summary>Foi para a Lixeira (recuperável).</summary>
            Recycled,

            /// <summary>Foi apagado de vez (o usuário/config pediu assim).</summary>
            DeletedPermanently,

            /// <summary>O usuário cancelou o diálogo de confirmação.</summary>
            CancelledByUser,

            /// <summary>Falhou — o item NÃO foi removido.</summary>
            Failed
        }

        /// <summary>
        /// Envia UM caminho para a Lixeira (ou apaga definitivamente).
        /// </summary>
        /// <param name="path">Arquivo ou pasta.</param>
        /// <param name="permanent">true = apagar sem Lixeira (equivale ao DelToBin=0 do Revo).</param>
        public static RecycleResult Recycle(string path, bool permanent, out string motivo)
        {
            motivo = "";
            if (string.IsNullOrWhiteSpace(path))
            {
                motivo = "caminho vazio";
                return RecycleResult.Failed;
            }

            // 0x414 = ALLOWUNDO|NOERRORUI|NODIRFLAGS  (Revo, DelToBin=1)
            // 0x454 = + RENAMEONCOLLISION            (Revo, DelToBin=0)
            ushort flags = (ushort)(FOF_ALLOWUNDO | FOF_NOERRORUI | FOF_NODIRFLAGS);
            if (permanent) flags |= FOF_RENAMEONCOLLISION;

            IntPtr buf = IntPtr.Zero;
            try
            {
                // lista terminada com NUL NUL
                buf = Marshal.StringToHGlobalUni(path + "\0\0");
                var op = new SHFILEOPSTRUCT
                {
                    hwnd = IntPtr.Zero,
                    wFunc = FO_DELETE,
                    pFrom = buf,
                    pTo = IntPtr.Zero,
                    fFlags = flags,
                    fAnyOperationsAborted = false,
                    hNameMappings = IntPtr.Zero,
                    lpszProgressTitle = IntPtr.Zero
                };

                int hr = SHFileOperation(ref op);

                if (op.fAnyOperationsAborted)
                {
                    // O USUÁRIO recusou. Com a implementação anterior isso era
                    // contado como "removido" e o item ficava no disco.
                    motivo = "o usuario cancelou a confirmacao";
                    return RecycleResult.CancelledByUser;
                }

                if (hr != 0)
                {
                    motivo = $"SHFileOperationW retornou {hr} ({new Win32Exception(hr).Message})";
                    return RecycleResult.Failed;
                }

                return permanent ? RecycleResult.DeletedPermanently : RecycleResult.Recycled;
            }
            catch (Exception ex)
            {
                motivo = ex.GetType().Name + ": " + ex.Message;
                return RecycleResult.Failed;
            }
            finally
            {
                if (buf != IntPtr.Zero) Marshal.FreeHGlobal(buf);
            }
        }
    }
}