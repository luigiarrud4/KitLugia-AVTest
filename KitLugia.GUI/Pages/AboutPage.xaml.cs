using System;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;

// Resolução de ambiguidade
using Application = System.Windows.Application;

namespace KitLugia.GUI.Pages
{
    public partial class AboutPage : Page
    {
        public AboutPage()
        {
            InitializeComponent();

            this.Unloaded += AboutPage_Unloaded;
        }


        public void Cleanup()
        {
            this.Unloaded -= AboutPage_Unloaded;


            this.DataContext = null;



        }

        private void AboutPage_Unloaded(object sender, RoutedEventArgs e)
        {
            Cleanup();
        }

        private void BtnLegacy_Click(object sender, RoutedEventArgs e)
        {
            // Define o nome do executável antigo que você compilou separadamente
            string legacyFileName = "KitLugia_Legacy.exe";

            // Pega o diretório onde o KitLugia.GUI.exe está rodando
            string currentPath = AppDomain.CurrentDomain.BaseDirectory;
            string fullPath = Path.Combine(currentPath, legacyFileName);

            if (File.Exists(fullPath))
            {
                try
                {
                    // Inicia o console antigo em nova janela
                    var psi = new ProcessStartInfo
                    {
                        FileName = fullPath,
                        UseShellExecute = true, // Necessário para abrir janela separada
                        Verb = "runas"          // Solicita Admin (pois o Legacy precisa)
                    };

                    Process.Start(psi);

                    // Avisa na UI principal
                    if (Application.Current.MainWindow is MainWindow mw)
                        mw.ShowSuccess("LEGACY", "Versão clássica iniciada com sucesso.");
                }
                catch (Exception ex)
                {
                    if (Application.Current.MainWindow is MainWindow mw)
                        mw.ShowError("ERRO", $"Falha ao iniciar: {ex.Message}");
                }
            }
            else
            {
                // Erro se o arquivo não existir
                if (Application.Current.MainWindow is MainWindow mw)
                {
                    mw.ShowError("ARQUIVO NÃO ENCONTRADO",
                        $"O arquivo '{legacyFileName}' não foi encontrado.\n" +
                        "Coloque o executável da versão antiga na mesma pasta deste programa.");
                }
            }
        }
    }
}