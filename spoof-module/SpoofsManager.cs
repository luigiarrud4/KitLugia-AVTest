using Microsoft.Win32;
using System.Runtime.Versioning;

namespace KitLugia.SpoofExtension;

[SupportedOSPlatform("windows")]
public static class SpoofsManager
{
    // =================================================================
    // HARDWARE IDS (ring-3) — rotaciona GUIDs e IDs do registro
    // =================================================================
    public static (int Count, List<string> Details) RotateHardwareIds()
    {
        int count = 0;
        var details = new List<string>();

        void SetGuid(string path, string name)
        {
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(path, writable: true);
                if (key != null)
                {
                    string newGuid = Guid.NewGuid().ToString("D").ToUpperInvariant();
                    key.SetValue(name, newGuid, RegistryValueKind.String);
                    details.Add($"{name}: {newGuid[..8]}...");
                    count++;
                }
                else details.Add($"{name}: chave n\u00e3o encontrada");
            }
            catch (Exception ex) { details.Add($"{name}: {ex.Message}"); }
        }

        void SetString(string path, string name, string value)
        {
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(path, writable: true);
                if (key != null)
                {
                    key.SetValue(name, value, RegistryValueKind.String);
                    details.Add($"{name}: {value}");
                    count++;
                }
                else details.Add($"{name}: chave n\u00e3o encontrada");
            }
            catch (Exception ex) { details.Add($"{name}: {ex.Message}"); }
        }

        SetGuid(@"SOFTWARE\Microsoft\Cryptography", "MachineGuid");
        SetGuid(@"SYSTEM\CurrentControlSet\Control\IDConfigDB\Hardware Profiles\0001", "HwProfileGuid");
        SetGuid(@"SOFTWARE\Microsoft\SQMClient", "MachineId");
        SetGuid(@"SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate", "SusClientId");
        string fakePid = $"{Random.Shared.Next(10000, 99999)}-{Random.Shared.Next(10000, 99999)}-{Random.Shared.Next(10000, 99999)}-{Random.Shared.Next(10000, 99999)}";
        SetString(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion", "ProductId", fakePid);
        int fakeInstall = (int)DateTimeOffset.UtcNow.AddDays(-Random.Shared.Next(365, 1095)).ToUnixTimeSeconds();
        SetString(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion", "InstallDate", fakeInstall.ToString());

        return (count, details);
    }

    // =================================================================
    // GPU IDENTIFIERS — modifica AdapterString + ChipType no registro
    // =================================================================
    public static (int Count, List<string> Details) SpoofGpuIdentifiers()
    {
        int count = 0;
        var details = new List<string>();
        string basePath = @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}";

        try
        {
            using var baseKey = Registry.LocalMachine.OpenSubKey(basePath);
            if (baseKey == null) { details.Add("Chave de v\u00eddeo n\u00e3o encontrada"); return (0, details); }

            foreach (var sub in baseKey.GetSubKeyNames())
            {
                if (sub.Length != 4 || !int.TryParse(sub, out _)) continue;
                string fullPath = $@"{basePath}\{sub}";
                try
                {
                    using var key = Registry.LocalMachine.OpenSubKey(fullPath, writable: true);
                    if (key == null) continue;
                    if (key.GetValue("DriverDesc") == null) continue;

                    if (key.GetValue("HardwareInformation.AdapterString") is string)
                    {
                        string newAdapter = Random.Shared.Next(2) == 0
                            ? $"NVIDIA GeForce RTX {Random.Shared.Next(4050, 5095)}"
                            : $"AMD Radeon RX {Random.Shared.Next(7600, 7999)} XT";
                        key.SetValue("HardwareInformation.AdapterString", newAdapter, RegistryValueKind.String);
                        details.Add($"GPU {sub}: AdapterString \u2192 {newAdapter}");
                        count++;
                    }
                    if (key.GetValue("HardwareInformation.ChipType") is string)
                    {
                        string newChip = Random.Shared.Next(2) == 0
                            ? $"GeForce RTX {Random.Shared.Next(4050, 5095)}"
                            : $"Radeon RX {Random.Shared.Next(7600, 7999)}";
                        key.SetValue("HardwareInformation.ChipType", newChip, RegistryValueKind.String);
                        details.Add($"GPU {sub}: ChipType \u2192 {newChip}");
                        count++;
                    }
                }
                catch (Exception ex) { details.Add($"GPU {sub}: {ex.Message}"); }
            }
        }
        catch (Exception ex) { details.Add($"GPU: {ex.Message}"); }

        return (count, details);
    }

    // =================================================================
    // SMBIOS REGISTRY MIRROR
    // =================================================================
    public static (int Count, List<string> Details) SpoofSmbiosRegistry()
    {
        int count = 0;
        var details = new List<string>();
        var rng = Random.Shared;

        void SetSmbiosValue(string path, string name, string value)
        {
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(path, writable: true);
                if (key != null)
                {
                    key.SetValue(name, value, RegistryValueKind.String);
                    details.Add($"{name}: {value}");
                    count++;
                }
            }
            catch (Exception ex) { details.Add($"{name}: {ex.Message}"); }
        }

        string RandomAlphanum(int len) => string.Concat(Enumerable.Range(0, len).Select(_ =>
            "ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789"[rng.Next(36)]));

        SetSmbiosValue(@"HARDWARE\DESCRIPTION\System\BIOS", "SystemSerialNumber", RandomAlphanum(10));
        SetSmbiosValue(@"HARDWARE\DESCRIPTION\System\BIOS", "BaseBoardSerialNumber", RandomAlphanum(12));
        SetSmbiosValue(@"HARDWARE\DESCRIPTION\System\BIOS", "BIOSSerialNumber", RandomAlphanum(10));
        SetSmbiosValue(@"HARDWARE\DESCRIPTION\System\BIOS", "ChassisSerialNumber", RandomAlphanum(10));
        SetSmbiosValue(@"HARDWARE\DESCRIPTION\System\BIOS", "SystemUUID", Guid.NewGuid().ToString("D").ToUpperInvariant());

        return (count, details);
    }

    // =================================================================
    // DISK VOLUME SERIAL — patch no boot sector (NTFS/FAT32)
    // =================================================================
    public static (bool Success, string Message) PatchVolumeSerial(char driveLetter, uint newSerial)
    {
        try
        {
            string path = $@"\\.\{driveLetter}:";
            using var fs = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
            var sector = new byte[512];
            fs.Read(sector, 0, 512);

            string fsType = "";
            int serialOffs = -1;

            if (sector[3] == 0x4E && sector[4] == 0x54 && sector[5] == 0x46 && sector[6] == 0x53)
            { fsType = "NTFS"; serialOffs = 0x48; }
            else if (sector[82] == 0x46 && sector[83] == 0x41 && sector[84] == 0x54 && sector[85] == 0x33 && sector[86] == 0x32)
            { fsType = "FAT32"; serialOffs = 0x43; }
            else if (sector[54] == 0x46 && sector[55] == 0x41 && sector[56] == 0x54)
            { fsType = "FAT"; serialOffs = 0x27; }

            if (serialOffs < 0)
                return (false, "Filesystem n\u00e3o suportado. Use NTFS, FAT32 ou FAT.");

            for (int i = 0; i < 4; i++)
                sector[serialOffs + i] = (byte)((newSerial >> (i * 8)) & 0xFF);

            fs.Seek(0, SeekOrigin.Begin);
            fs.Write(sector, 0, 512);
            fs.Flush();

            string serialStr = $"{newSerial >> 16:X4}-{newSerial & 0xFFFF:X4}";
            return (true, $"Serial alterado para {serialStr} ({fsType}). Aplica ap\u00f3s reinicializar.");
        }
        catch (Exception ex)
        {
            return (false, $"Erro: {ex.Message}");
        }
    }

    // =================================================================
    // DISK SERIALS (registry cache)
    // =================================================================
    public static (int Count, List<string> Details) SpoofDiskSerials()
    {
        int count = 0;
        var details = new List<string>();
        var rng = Random.Shared;

        void RandomizeSerial(string path)
        {
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(path, writable: true);
                if (key == null) return;
                foreach (var valName in key.GetValueNames())
                {
                    string lower = valName.ToLowerInvariant();
                    if (lower.Contains("serial") || lower.Contains("serialnumber"))
                    {
                        if (key.GetValue(valName) is string old && !string.IsNullOrEmpty(old))
                        {
                            string fake = string.Concat(Enumerable.Range(0, old.Length).Select(_ => rng.Next(16).ToString("X")));
                            key.SetValue(valName, fake, RegistryValueKind.String);
                            details.Add($"{path}\\{valName}: {old[..Math.Min(old.Length, 8)]}... \u2192 {fake[..Math.Min(fake.Length, 8)]}...");
                            count++;
                        }
                    }
                }
            }
            catch (Exception ex) { details.Add($"{path}: {ex.Message}"); }
        }

        string[] enumBases = {
            @"SYSTEM\CurrentControlSet\Enum\IDE",
            @"SYSTEM\CurrentControlSet\Enum\STORAGE",
            @"SYSTEM\CurrentControlSet\Enum\SCSI",
            @"SYSTEM\CurrentControlSet\Enum\PCIIDE"
        };

        foreach (var basePath in enumBases)
        {
            try
            {
                using var baseKey = Registry.LocalMachine.OpenSubKey(basePath);
                if (baseKey == null) continue;
                foreach (var dev in baseKey.GetSubKeyNames())
                {
                    string devPath = $@"{basePath}\{dev}";
                    try
                    {
                        using var devKey = Registry.LocalMachine.OpenSubKey(devPath);
                        if (devKey == null) continue;
                        foreach (var inst in devKey.GetSubKeyNames())
                        {
                            string instPath = $@"{devPath}\{inst}";
                            RandomizeSerial(instPath);
                        }
                    }
                    catch { }
                }
            }
            catch { }
        }

        return (count, details);
    }

    // =================================================================
    // USB/HID DEVICE SERIALS (registry cache)
    // =================================================================
    public static (int Count, List<string> Details) SpoofUsbSerials()
    {
        int count = 0;
        var details = new List<string>();
        var rng = Random.Shared;

        void RandomizeUsbSerial(string path)
        {
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(path, writable: true);
                if (key == null) return;
                foreach (var subName in key.GetSubKeyNames())
                {
                    string fullSub = $@"{path}\{subName}";
                    try
                    {
                        using var subKey = Registry.LocalMachine.OpenSubKey(fullSub, writable: true);
                        if (subKey == null) continue;

                        if (subKey.GetValue("SerialNumber") is string serial)
                        {
                            string fake = string.Concat(Enumerable.Range(0, serial.Length).Select(_ =>
                                "0123456789ABCDEF"[rng.Next(16)]));
                            subKey.SetValue("SerialNumber", fake, RegistryValueKind.String);
                            details.Add($"{subName}: SerialNumber \u2192 {fake[..Math.Min(fake.Length, 8)]}...");
                            count++;
                        }

                        if (subKey.GetValue("HardwareID") is string[] hwids)
                        {
                            var newHwids = hwids.Select(h => h.Contains("&")
                                ? string.Concat(h.Split('&')[0], "&", string.Concat(Enumerable.Range(0, 8).Select(_ =>
                                    rng.Next(16).ToString("X"))))
                                : h).ToArray();
                            subKey.SetValue("HardwareID", newHwids, RegistryValueKind.MultiString);
                        }
                    }
                    catch { }
                }
            }
            catch (Exception ex) { details.Add($"{path}: {ex.Message}"); }
        }

        string[] usbPaths = {
            @"SYSTEM\CurrentControlSet\Enum\USB",
            @"SYSTEM\CurrentControlSet\Enum\USBSTOR",
            @"SYSTEM\CurrentControlSet\Enum\HID",
        };

        foreach (var p in usbPaths)
            RandomizeUsbSerial(p);

        return (count, details);
    }

    // =================================================================
    // SYSTEM INFORMATION — ComputerHardwareId, etc.
    // =================================================================
    public static (int Count, List<string> Details) SpoofSystemInformation()
    {
        int count = 0;
        var details = new List<string>();
        var rng = Random.Shared;

        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(
                @"SYSTEM\CurrentControlSet\Control\SystemInformation", writable: true);
            if (key != null)
            {
                if (key.GetValue("ComputerHardwareId") is string hid)
                {
                    string fake = Guid.NewGuid().ToString("B").ToUpperInvariant();
                    key.SetValue("ComputerHardwareId", fake, RegistryValueKind.String);
                    details.Add($"ComputerHardwareId: {hid[..8]}... \u2192 {fake[..8]}...");
                    count++;
                }
                if (key.GetValue("ComputerHardwareId2") is string hid2)
                {
                    string fake2 = Guid.NewGuid().ToString("B").ToUpperInvariant();
                    key.SetValue("ComputerHardwareId2", fake2, RegistryValueKind.String);
                    details.Add($"ComputerHardwareId2: {hid2[..8]}... \u2192 {fake2[..8]}...");
                    count++;
                }
            }
        }
        catch (Exception ex) { details.Add($"SystemInformation: {ex.Message}"); }

        try
        {
            using var cpuKey = Registry.LocalMachine.OpenSubKey(
                @"HARDWARE\DESCRIPTION\System\CentralProcessor\0", writable: true);
            if (cpuKey != null && cpuKey.GetValue("ProcessorNameString") is string cpuName)
            {
                string fakeCpu = cpuName;
                if (cpuName.Contains("Intel"))
                    fakeCpu = $"Intel(R) Core(TM) i{rng.Next(5, 9)}-{rng.Next(10000, 15000)} CPU @ {rng.Next(200, 500) / 100.0:F1}GHz";
                else if (cpuName.Contains("AMD"))
                    fakeCpu = $"AMD Ryzen {rng.Next(3, 9)} {rng.Next(100, 9999)}X @ {rng.Next(200, 500) / 100.0:F1}GHz";
                cpuKey.SetValue("ProcessorNameString", fakeCpu, RegistryValueKind.String);
                details.Add($"ProcessorNameString: {fakeCpu}");
                count++;
            }
        }
        catch { }

        return (count, details);
    }

    // =================================================================
    // REGISTRY TRACE CLEANER
    // =================================================================
    public static (int Count, List<string> Details) CleanRegistryTraces()
    {
        int count = 0;
        var details = new List<string>();

        void DeleteValueIfExists(string path, string value)
        {
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(path, writable: true);
                if (key != null && key.GetValue(value) != null)
                {
                    key.DeleteValue(value);
                    details.Add($"{path}\\{value} removido");
                    count++;
                }
            }
            catch (Exception ex) { details.Add($"{path}\\{value}: {ex.Message}"); }
        }

        string netClass = @"SYSTEM\CurrentControlSet\Control\Class\{4d36e972-e325-11ce-bfc1-08002be10318}";
        try
        {
            using var baseKey = Registry.LocalMachine.OpenSubKey(netClass);
            if (baseKey != null)
            {
                foreach (var sub in baseKey.GetSubKeyNames().Where(s => s.Length == 4))
                {
                    string path = $@"{netClass}\{sub}";
                    DeleteValueIfExists(path, "OriginalNetworkAddress");
                    DeleteValueIfExists(path, "BackupNetworkAddress");
                    DeleteValueIfExists(path, "PermanentAddressBackup");
                }
            }
        }
        catch { }

        try
        {
            using var sqm = Registry.LocalMachine.OpenSubKey(
                @"SOFTWARE\Microsoft\SQMClient", writable: true);
            if (sqm != null)
            {
                if (sqm.GetValue("MachineId") != null)
                {
                    sqm.DeleteValue("MachineId");
                    details.Add("SQMClient\\MachineId removido (ser\u00e1 recriado)");
                    count++;
                }
            }
        }
        catch { }

        try
        {
            using var winUpdate = Registry.LocalMachine.OpenSubKey(
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate", writable: true);
            if (winUpdate != null)
            {
                foreach (var val in winUpdate.GetValueNames())
                {
                    if (val.Contains("SusClientId", StringComparison.OrdinalIgnoreCase))
                    {
                        winUpdate.DeleteValue(val);
                        details.Add($"WindowsUpdate\\{val} removido");
                        count++;
                    }
                }
            }
        }
        catch { }

        return (count, details);
    }

    // =================================================================
    // SPOOF ALL — executa todos os spoofs em sequência
    // =================================================================
    public static (int TotalChanges, string FullLog) SpoofAll()
    {
        var allDetails = new List<string>();
        int total = 0;

        allDetails.Add("=== Registry Trace Cleaner ===");
        var (cClean, dClean) = CleanRegistryTraces();
        total += cClean; allDetails.AddRange(dClean);

        allDetails.Add("\n=== HWIDs (registry) ===");
        var (c1, d1) = RotateHardwareIds();
        total += c1; allDetails.AddRange(d1);

        allDetails.Add("\n=== GPU ===");
        var (c2, d2) = SpoofGpuIdentifiers();
        total += c2; allDetails.AddRange(d2);

        allDetails.Add("\n=== SMBIOS (registry) ===");
        var (c3, d3) = SpoofSmbiosRegistry();
        total += c3; allDetails.AddRange(d3);

        allDetails.Add("\n=== Volume Serial (C:) ===");
        uint newSerial = (uint)Random.Shared.Next();
        var volResult = PatchVolumeSerial('C', newSerial);
        allDetails.Add(volResult.Success ? $"Volume C: \u2192 {volResult.Message}" : $"Volume C: falhou \u2014 {volResult.Message}");
        if (volResult.Success) total++;

        allDetails.Add("\n=== Disk Serials (registry cache) ===");
        var (cDisk, dDisk) = SpoofDiskSerials();
        total += cDisk; allDetails.AddRange(dDisk);

        allDetails.Add("\n=== USB/HID Serials (registry cache) ===");
        var (cUsb, dUsb) = SpoofUsbSerials();
        total += cUsb; allDetails.AddRange(dUsb);

        allDetails.Add("\n=== System Information ===");
        var (cSys, dSys) = SpoofSystemInformation();
        total += cSys; allDetails.AddRange(dSys);

        return (total, string.Join("\n", allDetails));
    }
}
