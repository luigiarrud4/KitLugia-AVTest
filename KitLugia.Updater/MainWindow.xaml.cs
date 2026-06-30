using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;

namespace KitLugia.Updater;

public partial class MainWindow : Window
{
    private readonly string[] _args;
    private readonly ObservableCollection<StepItem> _steps = new();

    public MainWindow(string[] args)
    {
        _args = args;
        InitializeComponent();
        StepsList.ItemsSource = _steps;
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        RunUpdate();
    }

    private async void RunUpdate()
    {
        if (_args.Length < 3)
        {
            ShowError("USO: KitLugia.Updater <zipPath> <mainPid> <mainExePath> [sha256]\nArgumentos insuficientes.");
            return;
        }

        string zipPath = _args[0];
        int mainPid = int.Parse(_args[1]);
        string mainExePath = _args[2];
        string expectedHash = _args.Length > 3 ? _args[3] : null;
        string appDir = Path.GetDirectoryName(mainExePath);
        string logPath = Path.Combine(appDir, "update.log");

        try
        {
            SetStatus("Verificando integridade...", 8);
            AddStep("📦", "Verificando hash...", false);
            if (!string.IsNullOrEmpty(expectedHash))
            {
                string actualHash = await Task.Run(() => ComputeSha256(zipPath));
                if (!string.Equals(actualHash, expectedHash, StringComparison.OrdinalIgnoreCase))
                {
                    Log(logPath, $"HASH MISMATCH. Expected: {expectedHash}, Actual: {actualHash}");
                    SetStepDone(0, true);
                    ShowError($"Hash inválido!\nEsperado: {expectedHash}\nCalculado: {actualHash}");
                    return;
                }
                SetStepDone(0, true);
            }
            else
            {
                SetStepDone(0, true);
            }

            SetStatus("Aguardando fechamento do KitLugia...", 25);
            AddStep("⏳", "Aguardando fechamento...", false);
            await Task.Run(() =>
            {
                try
                {
                    var mainProcess = Process.GetProcessById(mainPid);
                    if (!mainProcess.WaitForExit(60000))
                    {
                        mainProcess.Kill();
                        mainProcess.WaitForExit(5000);
                    }
                }
                catch (ArgumentException) { }
            });
            SetStepDone(1, true);
            await Task.Delay(500);

            // Ler versão antiga antes de copiar
            string oldVersion = GetExeVersion(mainExePath);

            SetStatus("Extraindo arquivos...", 45);
            AddStep("📂", "Extraindo ZIP...", false);
            string extractDir = Path.Combine(Path.GetTempPath(), $"KitLugia_Update_{Guid.NewGuid():N}");
            await Task.Run(() =>
            {
                Directory.CreateDirectory(extractDir);
                ZipFile.ExtractToDirectory(zipPath, extractDir, overwriteFiles: true);
            });
            SetStepDone(2, true);

            SetStatus("Aplicando atualização...", 65);
            AddStep("📋", "Copiando arquivos...", false);
            string newVersion = GetExeVersion(Path.Combine(extractDir, Path.GetFileName(mainExePath)));
            await Task.Run(() => CopyDirectory(extractDir, appDir, logPath));
            SetStepDone(3, true);

            SetStatus("Limpando temporários...", 80);
            AddStep("🧹", "Limpando temporários...", false);
            await Task.Run(() =>
            {
                try { Directory.Delete(extractDir, true); } catch { }
                try { File.Delete(zipPath); } catch { }
            });
            SetStepDone(4, true);

            // Escrever UPDATE_COMPLETE.txt
            try
            {
                var updateInfo = new { OldVersion = oldVersion, NewVersion = newVersion };
                string json = JsonSerializer.Serialize(updateInfo);
                File.WriteAllText(Path.Combine(appDir, "UPDATE_COMPLETE.txt"), json);
            }
            catch (Exception ex)
            {
                Log(logPath, $"Warning: could not write UPDATE_COMPLETE.txt: {ex.Message}");
            }

            SetStatus("Iniciando nova versão...", 95);
            AddStep("🚀", "Iniciando nova versão...", false);
            await Task.Run(() =>
            {
                try
                {
                    var psi = new ProcessStartInfo
                    {
                        FileName = mainExePath,
                        UseShellExecute = true
                    };
                    Process.Start(psi);
                    SetStepDone(5, true);
                }
                catch (Exception ex)
                {
                    Log(logPath, $"Restart failed: {ex}");
                    throw;
                }
            });

            SetStatus("Concluído!", 100);
            TxtLogo.Foreground = new SolidColorBrush(Color.FromRgb(67, 160, 71));
            TxtLogo.Text = "✓ ATUALIZAÇÃO CONCLUÍDA";
            TxtStatus.Text = "KitLugia foi atualizado com sucesso!";
            BtnOk.IsEnabled = true;
            BtnOk.Visibility = Visibility.Visible;
            TxtInfo.Text = "A nova versão será iniciada em alguns instantes.";
        }
        catch (Exception ex)
        {
            Log(logPath, $"FATAL: {ex}");
            ShowError($"Erro na atualização:\n{ex.Message}\n\nLog salvo em: update.log");
        }
    }

    private void AddStep(string icon, string text, bool done)
    {
        Dispatcher.Invoke(() => _steps.Add(new StepItem
        {
            Icon = done ? "✓" : icon,
            Text = text,
            Color = done ? new SolidColorBrush(Color.FromRgb(67, 160, 71)) : new SolidColorBrush(Color.FromRgb(200, 200, 200))
        }));
    }

    private void SetStepDone(int index, bool success)
    {
        Dispatcher.Invoke(() =>
        {
            if (index < _steps.Count)
            {
                _steps[index].Icon = success ? "✓" : "✗";
                _steps[index].Color = success
                    ? new SolidColorBrush(Color.FromRgb(67, 160, 71))
                    : new SolidColorBrush(Color.FromRgb(229, 57, 53));
            }
        });
    }

    private void SetStatus(string text, int progress)
    {
        Dispatcher.Invoke(() =>
        {
            TxtStatus.Text = text;
            ProgressFill.Width = (Width - 60) * progress / 100.0;
        });
    }

    private void ShowError(string message)
    {
        Dispatcher.Invoke(() =>
        {
            TxtLogo.Foreground = new SolidColorBrush(Color.FromRgb(229, 57, 53));
            TxtLogo.Text = "✗ ERRO NA ATUALIZAÇÃO";
            TxtStatus.Text = message;
            BtnOk.IsEnabled = true;
            BtnOk.Visibility = Visibility.Visible;
            TxtInfo.Text = "Verifique o log para mais detalhes.";
        });
    }

    private void BtnOk_Click(object sender, RoutedEventArgs e)
    {
        Application.Current.Shutdown();
    }

    static void CopyDirectory(string sourceDir, string destDir, string logPath)
    {
        foreach (string file in Directory.GetFiles(sourceDir, "*", SearchOption.AllDirectories))
        {
            string relative = Path.GetRelativePath(sourceDir, file);
            string dest = Path.Combine(destDir, relative);

            string? dir = Path.GetDirectoryName(dest);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            string name = Path.GetFileName(file);
            string ext = Path.GetExtension(file).ToLowerInvariant();
            if (ext is ".pdb" or ".xml" or ".config" or ".log" or ".tmp")
                continue;
            if (name is "KitLugia.Updater.exe" or "KitLugia.Updater.dll")
                continue;

            try { File.Copy(file, dest, overwrite: true); }
            catch (Exception ex) { Log(logPath, $"Warning: could not copy {relative}: {ex.Message}"); }
        }
    }

    static string ComputeSha256(string filePath)
    {
        using var sha256 = SHA256.Create();
        using var stream = File.OpenRead(filePath);
        byte[] hash = sha256.ComputeHash(stream);
        return Convert.ToHexStringLower(hash);
    }

    static string GetExeVersion(string exePath)
    {
        try
        {
            if (File.Exists(exePath))
                return FileVersionInfo.GetVersionInfo(exePath).FileVersion ?? "0.0.0.0";
        }
        catch { }
        return "0.0.0.0";
    }

    static void Log(string logPath, string message)
    {
        try
        {
            File.AppendAllText(logPath, $"[{DateTime.Now:HH:mm:ss}] {message}{Environment.NewLine}");
        }
        catch { }
    }
}

public class StepItem : INotifyPropertyChanged
{
    private string _icon;
    private string _text;
    private Brush _color;

    public string Icon { get => _icon; set { _icon = value; OnPropertyChanged(); } }
    public string Text { get => _text; set { _text = value; OnPropertyChanged(); } }
    public Brush Color { get => _color; set { _color = value; OnPropertyChanged(); } }

    public event PropertyChangedEventHandler? PropertyChanged;
    protected void OnPropertyChanged([System.Runtime.CompilerServices.CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
