using System;
using System.Windows.Forms; // Aqui é permitido, e SÓ AQUI.

namespace KitLugia.GUI
{
    // Classe isolada para lidar com janelas de arquivo/pasta
    public static class DialogHelper
    {
        public static string? PickFolder(string description = "Selecione uma pasta")
        {
            using (var dialog = new FolderBrowserDialog())
            {
                dialog.Description = description;
                dialog.UseDescriptionForTitle = true;

                if (dialog.ShowDialog() == DialogResult.OK && !string.IsNullOrWhiteSpace(dialog.SelectedPath))
                {
                    return dialog.SelectedPath;
                }
            }
            return null;
        }

        public static string? PickFile(string filter = "Todos os arquivos (*.*)|*.*")
        {
            using (var dialog = new OpenFileDialog())
            {
                dialog.Filter = filter;
                dialog.CheckFileExists = true;

                if (dialog.ShowDialog() == DialogResult.OK)
                {
                    return dialog.FileName;
                }
            }
            return null;
        }

        public static string? SaveFile(string fileName, string filter = "Texto (*.txt)|*.txt")
        {
            using (var dialog = new SaveFileDialog())
            {
                dialog.FileName = fileName;
                dialog.Filter = filter;

                if (dialog.ShowDialog() == DialogResult.OK)
                {
                    return dialog.FileName;
                }
            }
            return null;
        }
    }
}