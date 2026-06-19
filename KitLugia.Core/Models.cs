using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.Versioning;

namespace KitLugia.Core
{
    // --- ENUMS ---
    public enum TweakType { Registry, Service, Mouse, Bcd, PageFile, GpuInterruptPriority }
    public enum TweakStatus { OK, MODIFIED, ERROR, NOT_FOUND }
    public enum StartupStatus { Disabled, Enabled, Elevated, TurboBoot }
    public enum ActionType { BuiltIn, GenericCommand, Script }
    public enum ServiceSafetyLevel { Safe, Caution, Dangerous, Unknown }

    // --- REPAIR ACTION ---
    public class RepairAction
    {
        public string Id { get; set; } = Guid.NewGuid().ToString();
        public string Name { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public string Category { get; set; } = "Geral";
        public string Icon { get; set; } = "";
        public Action? Execute { get; set; }
        public bool IsDangerous { get; set; } = false;
        public bool IsSelected { get; set; } = false;
        public bool IsSlow { get; set; } = false;
    }

    // --- STARTUP APP ---
    public class StartupAppDetails
    {
        public string Name { get; set; }
        public string FullCommand { get; set; }
        public string Location { get; set; }
        public StartupStatus Status { get; set; }

        public StartupAppDetails(string name, string fullCommand, string location, StartupStatus status)
        {
            Name = name;
            FullCommand = fullCommand;
            Location = location;
            Status = status;
        }

        public string ExePath
        {
            get
            {
                StartupManager.ExtractCommandParts(FullCommand, out string? path, out _);
                return path ?? "";
            }
        }

        public string Arguments
        {
            get
            {
                StartupManager.ExtractCommandParts(FullCommand, out _, out string? args);
                return args ?? "";
            }
        }
    }

    public record ServiceInfo(string Name, string DisplayName, string Description, string Status, string StartMode, ServiceSafetyLevel Safety);

    // --- DRIVER ITEM ---
    [SupportedOSPlatform("windows")]
    public class DriverItem : INotifyPropertyChanged
    {
        public string DeviceName { get; set; } = "";
        public string Provider { get; set; } = "";
        public string Version { get; set; } = "";
        public string Date { get; set; } = "";
        public string InfName { get; set; } = "";
        public string HardwareId { get; set; } = "";
        public object? WindowsUpdateObj { get; set; }

        // Propriedade auxiliar para ordenação correta da data
        public DateTime DateAsDateTime
        {
            get
            {
                if (DateTime.TryParse(Date, out DateTime result))
                    return result;
                return DateTime.MinValue;
            }
        }

        // Campos de automação
        private bool _isSelected;
        public bool IsSelected
        {
            get => _isSelected;
            set { _isSelected = value; OnPropertyChanged(nameof(IsSelected)); }
        }

        private string _updateStatus = "Verificado";
        public string UpdateStatus
        {
            get => _updateStatus;
            set { _updateStatus = value; OnPropertyChanged(nameof(UpdateStatus)); }
        }

        public string Icon
        {
            get
            {
                if (string.IsNullOrEmpty(Provider)) return "🔌";
                if (Provider.Contains("NVIDIA") || Provider.Contains("AMD") || Provider.Contains("Intel")) return "📺";
                if (Provider.Contains("Realtek") || Provider.Contains("Audio") || Provider.Contains("Sound")) return "🔊";
                if (Provider.Contains("Ethernet") || Provider.Contains("Wireless") || Provider.Contains("Network")) return "🌐";
                if (Provider.Contains("Bluetooth")) return "🦷";
                if (Provider.Contains("USB")) return "🔌";
                return "⚙️";
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    // --- OUTROS RECORDS ---
    public record PowerPlanInfo(string Guid, string Name, bool IsActive);

    // --- BLOATWARE APP (modificado para suportar ícones e mais informações) ---
    public class BloatwareApp
    {
        public string DisplayName { get; set; } = "";
        public string PackageName { get; set; } = "";
        public bool IsInstalled { get; set; }
        public string StoreId { get; set; } = "";
        public object? Icon { get; set; } // BitmapSource ou ImageSource
        public string Publisher { get; set; } = "";
        public string Size { get; set; } = "";
        public string InstallDate { get; set; } = "";
        public bool IsSelected { get; set; } = false; // Para seleção múltipla
        public bool IsFolder { get; set; } = false;

        // Propriedade computada para compatibilidade com XAML DataTrigger
        public bool CanReinstall => !IsInstalled && !string.IsNullOrEmpty(StoreId);
    }

    public record PerformanceEvent(int EventId, string ItemName, long TimeTaken, string EventType, DateTime? TimeOfEvent, string SubType = "");
    public record InstalledProgram(string Name, string Publisher, string Version);
    public record DriverInfo(string DeviceName, string Provider, string Version, DateTime DriverDate);
    public record ScheduledTaskInfo(string Path, string Name, string Description, bool IsEnabled);
    public record SystemStats(string CpuName, float CpuLoad, float CpuTemp, string GpuName, float GpuTemp, double GpuVramUsed, double RamUsed, double RamTotal, string OsName, TimeSpan Uptime, List<StorageInfo> StorageDevices);
    public record StorageInfo(string Name, string HealthStatus, float Temp, string DriveLetter);

    public record BootAnalysisResult
    {
        public string ServiceStatusMessage { get; set; } = string.Empty;
        public PerformanceEvent? TotalTimeEvent { get; set; }
        public List<PerformanceEvent> SlowStartupItems { get; set; } = new();
        public List<PerformanceEvent> HighImpactApps { get; set; } = new();
    }

    // --- CLASSE TWEAK (ATUALIZADA COM DESCRIÇÃO) ---
    [SupportedOSPlatform("windows")]
    public class ScannableTweak
    {
        public string Name { get; set; } = string.Empty;

        // CAMPO NOVO QUE FALTAVA:
        public string Description { get; set; } = "Sem descrição disponível.";


        public bool IsOptional { get; set; } = false;

        public TweakType Type { get; set; } = TweakType.Registry;
        public string Category { get; set; } = string.Empty;
        public TweakStatus Status { get; set; }
        public string? KeyPath { get; set; }
        public string? ValueName { get; set; }
        public object? HarmfulValue { get; set; }
        public object? DefaultValue { get; set; }
        public RegistryValueKind ValueKind { get; set; } = RegistryValueKind.DWord;
        public string? ServiceName { get; set; }
        public string? HarmfulStartMode { get; set; }
        public string? DefaultStartMode { get; set; }
    }
    // --- WINBOOT MODELS ---
    public class DiskInfo
    {
        public uint Index { get; set; }
        public string Model { get; set; } = string.Empty;
        public string Interface { get; set; } = string.Empty;
        public ulong Size { get; set; }
        public string SizeString => $"{(Size / (1024.0 * 1024 * 1024)):F2} GB";
        public List<PartitionInfo> Partitions { get; set; } = new List<PartitionInfo>();
        public string DisplayName => $"Disco {Index}: {Model} ({Interface}) - {SizeString}";
    }

    public class PartitionInfo
    {
        public uint Index { get; set; }
        public uint DiskIndex { get; set; }
        public string Name { get; set; } = string.Empty;
        public string DriveLetter { get; set; } = string.Empty;
        public string Label { get; set; } = string.Empty;
        public string FileSystem { get; set; } = string.Empty;
        public ulong Size { get; set; }
        public ulong FreeSpace { get; set; }
        public string SizeString => $"{(Size / (1024.0 * 1024 * 1024)):F2} GB";
        public string FreeSpaceString => $"{(FreeSpace / (1024.0 * 1024 * 1024)):F2} GB";
        
        // Propriedade de segurança para o usuário
        public bool IsSafeToUse => Size >= (8L * 1024 * 1024 * 1024) && 
                                  !Label.Contains("Sistema", StringComparison.OrdinalIgnoreCase) && 
                                  !Label.Contains("EFI", StringComparison.OrdinalIgnoreCase) && 
                                  !Label.Contains("Reservado", StringComparison.OrdinalIgnoreCase);

        public string DisplayName => string.IsNullOrEmpty(DriveLetter) 
            ? $"Partição {Name} ({FileSystem}) - {SizeString}"
            : $"{DriveLetter} ({Label}) [{FileSystem}] - {FreeSpaceString} livres de {SizeString}";
    }
}
