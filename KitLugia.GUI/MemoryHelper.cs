using System.Diagnostics;
using System.Runtime.InteropServices;

namespace KitLugia.GUI
{
    /// <summary>
    /// Helper para gerenciamento de memória do Windows
    /// </summary>
    public static class MemoryHelper
    {
        [DllImport("psapi.dll")]
        private static extern bool EmptyWorkingSet(IntPtr hProcess);

        /// <summary>
        /// Força o Windows a liberar memória do Working Set do processo atual
        /// Isso reduz o uso de RAM visto no Task Manager sem afetar o GC Heap
        /// </summary>
        public static void TrimWorkingSet()
        {
            try
            {
                IntPtr handle = Process.GetCurrentProcess().Handle;
                EmptyWorkingSet(handle);
            }
            catch
            {
                // Ignora erros - se falhar, o Working Set permanece alto, mas o app continua funcionando
            }
        }
    }
}
