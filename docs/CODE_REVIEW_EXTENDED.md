# Code Review Ampliado — 2026-11-02

Arquivos analisados: 43
Arquivos com findings: 39

## KitLugia.Core/SystemTweaks.cs
- Arquivo grande (10362 linhas) — avaliar extração de helpers/serviços.
- 9 métodos >80 linhas (maior: 177).
- 2 TODO/FIXME/HACK/XXX: [8223, 8927]
- 286 números mágicos — avaliar constantes nomeadas.
- P/Invoke presente — validar parâmetros nativos.
- Directory.Delete — verificar guards.
- Process.Start — validar origem/argumentos.

## KitLugia.Core/WinbootManager.cs
- Arquivo grande (6954 linhas) — avaliar extração de helpers/serviços.
- 20 métodos >80 linhas (maior: 535).
- 1 TODO/FIXME/HACK/XXX: [4306]
- 201 números mágicos — avaliar constantes nomeadas.
- Directory.Delete — verificar guards.
- Process.Start — validar origem/argumentos.

## KitLugia.Core/Guardian.cs
- Arquivo grande (2830 linhas) — avaliar extração de helpers/serviços.
- 4 métodos >80 linhas (maior: 1205).
- 117 números mágicos — avaliar constantes nomeadas.
- P/Invoke presente — validar parâmetros nativos.

## KitLugia.Core/GeneralRepairManager.cs
- Arquivo grande (1805 linhas) — avaliar extração de helpers/serviços.
- 1 métodos >80 linhas (maior: 1791).
- 3 TODO/FIXME/HACK/XXX: [271, 1566, 1680]
- Directory.Delete — verificar guards.
- Process.Start — validar origem/argumentos.

## KitLugia.Core/StartupManager.cs
- Arquivo grande (2937 linhas) — avaliar extração de helpers/serviços.
- 5 métodos >80 linhas (maior: 473).
- Process.Start — validar origem/argumentos.

## KitLugia.Core/DriverUnlockService.cs
- Arquivo grande (1028 linhas) — avaliar extração de helpers/serviços.
- 1 métodos >80 linhas (maior: 250).
- P/Invoke presente — validar parâmetros nativos.
- Process.Start — validar origem/argumentos.

## KitLugia.Core/PartitionManager.cs
- Arquivo grande (2402 linhas) — avaliar extração de helpers/serviços.
- 5 métodos >80 linhas (maior: 124).
- 134 números mágicos — avaliar constantes nomeadas.

## KitLugia.Core/OOShutUpManager.cs
- Arquivo grande (1679 linhas) — avaliar extração de helpers/serviços.
- 1 métodos >80 linhas (maior: 1331).

## KitLugia.Core/NetworkExposureManager.cs
- 63 números mágicos — avaliar constantes nomeadas.

## KitLugia.Core/LatencyAnalyzer.cs
- Arquivo grande (1257 linhas) — avaliar extração de helpers/serviços.
- 1 métodos >80 linhas (maior: 85).
- 98 números mágicos — avaliar constantes nomeadas.
- P/Invoke presente — validar parâmetros nativos.

## KitLugia.Core/RegistryCleaner.cs
- Process.Start — validar origem/argumentos.

## KitLugia.Core/FileOpGuarantee.cs
- 1 TODO/FIXME/HACK/XXX: [134]
- P/Invoke presente — validar parâmetros nativos.

## KitLugia.Core/DiskConverterManager.cs
- Arquivo grande (1057 linhas) — avaliar extração de helpers/serviços.
- 1 métodos >80 linhas (maior: 134).
- 100 números mágicos — avaliar constantes nomeadas.

## KitLugia.Core/IsoEditorManager.cs
- 1 métodos >80 linhas (maior: 91).
- Directory.Delete — verificar guards.
- Process.Start — validar origem/argumentos.

## KitLugia.Core/WinpeBuilder.cs
- Arquivo grande (1843 linhas) — avaliar extração de helpers/serviços.
- 3 métodos >80 linhas (maior: 93).
- 2 TODO/FIXME/HACK/XXX: [952, 1527]
- 81 números mágicos — avaliar constantes nomeadas.
- Directory.Delete — verificar guards.
- Process.Start — validar origem/argumentos.

## KitLugia.Core/AdapterManager.cs
- 1 métodos >80 linhas (maior: 145).
- 2 TODO/FIXME/HACK/XXX: [448, 494]

## KitLugia.Core/BackgroundProcessManager.cs
- 3 TODO/FIXME/HACK/XXX: [192, 205, 228]

## KitLugia.Core/BcdRepairManager.cs
- 1 métodos >80 linhas (maior: 111).
- 2 TODO/FIXME/HACK/XXX: [148, 298]
- Process.Start — validar origem/argumentos.

## KitLugia.Core/BootableMediaManager.cs
- P/Invoke presente — validar parâmetros nativos.
- Process.Start — validar origem/argumentos.

## KitLugia.Core/NetworkManager.cs
- 1 TODO/FIXME/HACK/XXX: [738]
- Process.Start — validar origem/argumentos.

## KitLugia.GUI/Pages/AppsPage.xaml.cs
- Arquivo grande (3004 linhas) — avaliar extração de helpers/serviços.
- 4 métodos >80 linhas (maior: 142).
- Directory.Delete — verificar guards.

## KitLugia.GUI/Pages/ServicesPage.xaml.cs
- Arquivo grande (1714 linhas) — avaliar extração de helpers/serviços.
- 1 métodos >80 linhas (maior: 84).
- Process.Start — validar origem/argumentos.

## KitLugia.GUI/Pages/TweaksPage.xaml.cs
- Arquivo grande (2451 linhas) — avaliar extração de helpers/serviços.
- 3 métodos >80 linhas (maior: 354).
- 1 TODO/FIXME/HACK/XXX: [64]

## KitLugia.GUI/Pages/GameBoostPage.xaml.cs
- Arquivo grande (1958 linhas) — avaliar extração de helpers/serviços.
- 4 métodos >80 linhas (maior: 164).

## KitLugia.GUI/Pages/WinbootPage.xaml.cs
- Arquivo grande (1618 linhas) — avaliar extração de helpers/serviços.
- 4 métodos >80 linhas (maior: 421).

## KitLugia.GUI/Pages/IsoEditorPage.xaml.cs
- Arquivo grande (1275 linhas) — avaliar extração de helpers/serviços.
- 1 métodos >80 linhas (maior: 450).
- Directory.Delete — verificar guards.
- Process.Start — validar origem/argumentos.

## KitLugia.GUI/Pages/NetworkPage.xaml.cs
- Arquivo grande (1523 linhas) — avaliar extração de helpers/serviços.
- 1 métodos >80 linhas (maior: 118).
- 1 TODO/FIXME/HACK/XXX: [432]

## KitLugia.GUI/Pages/ProcessMonitorPage.xaml.cs
- Process.Start — validar origem/argumentos.

## KitLugia.GUI/Pages/PartitionsPage.xaml.cs
- Arquivo grande (1017 linhas) — avaliar extração de helpers/serviços.
- 3 métodos >80 linhas (maior: 104).
- 1 TODO/FIXME/HACK/XXX: [503]

## KitLugia.GUI/Pages/TraySettingsPage.xaml.cs
- Arquivo grande (1172 linhas) — avaliar extração de helpers/serviços.
- 1 métodos >80 linhas (maior: 188).
- 141 números mágicos — avaliar constantes nomeadas.
- P/Invoke presente — validar parâmetros nativos.

## KitLugia.GUI/Pages/IntegrityPage.xaml.cs
- 2 métodos >80 linhas (maior: 92).
- 3 TODO/FIXME/HACK/XXX: [182, 220, 655]

## KitLugia.GUI/Pages/CleanupPage.xaml.cs
- Arquivo grande (1221 linhas) — avaliar extração de helpers/serviços.
- 2 métodos >80 linhas (maior: 86).
- Process.Start — validar origem/argumentos.

## KitLugia.GUI/Pages/DashboardPage.xaml.cs
- Arquivo grande (1431 linhas) — avaliar extração de helpers/serviços.
- 3 métodos >80 linhas (maior: 102).
- 97 números mágicos — avaliar constantes nomeadas.

## KitLugia.GUI/Pages/ServerPage.xaml.cs
- 3 métodos >80 linhas (maior: 107).
- Process.Start — validar origem/argumentos.

## KitLugia.GUI/Pages/AllTweaksPage.xaml.cs
- 1 métodos >80 linhas (maior: 568).

## KitLugia.GUI/Pages/ExmTweaksPage.xaml.cs
- Arquivo grande (1463 linhas) — avaliar extração de helpers/serviços.
- 1 métodos >80 linhas (maior: 119).
- Directory.Delete — verificar guards.
- Process.Start — validar origem/argumentos.

## KitLugia.GUI/Pages/DiagnosticPage.xaml.cs
- 2 TODO/FIXME/HACK/XXX: [359, 414]

## KitLugia.GUI/Pages/DiskConverterPage.xaml.cs
- 1 métodos >80 linhas (maior: 83).

## KitLugia.GUI/Windows/HunterWindow.xaml.cs
- Arquivo grande (1057 linhas) — avaliar extração de helpers/serviços.
- 3 métodos >80 linhas (maior: 138).
- 1 TODO/FIXME/HACK/XXX: [725]
- 75 números mágicos — avaliar constantes nomeadas.
- P/Invoke presente — validar parâmetros nativos.
- Directory.Delete — verificar guards.
- Process.Start — validar origem/argumentos.


## KitLugia.GUI/Services/TrayIconService.cs
- Arquivo grande (8445 linhas) — avaliar extração de helpers/serviços.
- 179 números mágicos — avaliar constantes nomeadas.
- 12 métodos >80 linhas (maior: 389).
- 13 TODO/FIXME/HACK/XXX: [492, 1407, 1413, 1431, 2993]...
- P/Invoke presente (106 declarações) — validar parâmetros nativos.
- Process.Start — validar origem/argumentos (1 usos).


## KitLugia.Core/DeepUninstaller.cs
- Arquivo grande (5654 linhas) — avaliar extração de helpers/serviços.
- 81 números mágicos — avaliar constantes nomeadas.
- 12 métodos >80 linhas (maior: 302).
- P/Invoke presente (8 declarações) — validar parâmetros nativos.
- Process.Start — validar origem/argumentos (2 usos).


## KitLugia.GUI/Windows/TaskManager/KitTaskManagerWindow.xaml.cs
- Arquivo grande (4141 linhas) — avaliar extração de helpers/serviços.
- 67 números mágicos — avaliar constantes nomeadas.
- 8 métodos >80 linhas (maior: 406).
- 5 TODO/FIXME/HACK/XXX: [2388, 3117, 3187, 3329, 3499]
- P/Invoke presente (24 declarações) — validar parâmetros nativos.
- Process.Start — validar origem/argumentos (1 usos).


## KitLugia.GUI/MainWindow.xaml.cs
- Arquivo grande (3450 linhas) — avaliar extração de helpers/serviços.
- 84 números mágicos — avaliar constantes nomeadas.
- 6 métodos >80 linhas (maior: 305).
- 2 TODO/FIXME/HACK/XXX: [890, 1007]
- P/Invoke presente (4 declarações) — validar parâmetros nativos.
- Process.Start — validar origem/argumentos (2 usos).


## KitLugia.Core/TaskManager/NativeMetricsHelper.cs
- Arquivo grande (1931 linhas) — avaliar extração de helpers/serviços.
- 69 números mágicos — avaliar constantes nomeadas.
- 3 métodos >80 linhas (maior: 148).
- 2 TODO/FIXME/HACK/XXX: [18, 869]
- P/Invoke presente (74 declarações) — validar parâmetros nativos.


## KitLugia.Core/ForceStopUnlockService.cs
- Arquivo grande (2051 linhas) — avaliar extração de helpers/serviços.
- 31 números mágicos — avaliar constantes nomeadas.
- 7 métodos >80 linhas (maior: 304).
- 1 TODO/FIXME/HACK/XXX: [149]
- P/Invoke presente (37 declarações) — validar parâmetros nativos.
- Process.Start — validar origem/argumentos (6 usos).
- Directory.Delete — verificar guards (3 usos).


## KitLugia.GUI/Pages/WindowsSettings/StoreRemakePage.xaml.cs
- Arquivo grande (1691 linhas) — avaliar extração de helpers/serviços.
- 39 números mágicos — avaliar constantes nomeadas.
- 4 métodos >80 linhas (maior: 129).
- 1 TODO/FIXME/HACK/XXX: [432]


## KitLugia.Core/TaskManager/AudioGlitchMonitor.cs
- Arquivo grande (1472 linhas) — avaliar extração de helpers/serviços.
- 69 números mágicos — avaliar constantes nomeadas.
- 2 métodos >80 linhas (maior: 154).
- 2 TODO/FIXME/HACK/XXX: [586, 1267]
- P/Invoke presente (10 declarações) — validar parâmetros nativos.


## KitLugia.Core/TaskManager/LatencyMonitor.cs
- Arquivo grande (1417 linhas) — avaliar extração de helpers/serviços.
- 95 números mágicos — avaliar constantes nomeadas.
- 2 métodos >80 linhas (maior: 239).
- 6 TODO/FIXME/HACK/XXX: [117, 187, 431, 538, 708]...
- P/Invoke presente (31 declarações) — validar parâmetros nativos.


## KitLugia.GUI/Windows/TaskManager/KitTaskManager.Performance.cs
- Arquivo grande (1389 linhas) — avaliar extração de helpers/serviços.
- 53 números mágicos — avaliar constantes nomeadas.
- 4 métodos >80 linhas (maior: 198).
- P/Invoke presente (2 declarações) — validar parâmetros nativos.


## KitLugia.GUI/Windows/TaskManager/KitTaskManager.Summary.cs
- Arquivo grande (1421 linhas) — avaliar extração de helpers/serviços.
- 71 números mágicos — avaliar constantes nomeadas.
- 4 métodos >80 linhas (maior: 202).


## KitLugia.Core/TaskManager/StorageDiagnostics.cs
- Arquivo grande (1356 linhas) — avaliar extração de helpers/serviços.
- 47 números mágicos — avaliar constantes nomeadas.
- P/Invoke presente (10 declarações) — validar parâmetros nativos.


## KitLugia.GUI/Windows/TaskManager/KitTaskManager.Storage.cs
- Arquivo grande (1381 linhas) — avaliar extração de helpers/serviços.
- 40 números mágicos — avaliar constantes nomeadas.
- 2 métodos >80 linhas (maior: 113).
- 1 TODO/FIXME/HACK/XXX: [1372]
- Process.Start — validar origem/argumentos (1 usos).


## KitLugia.Core/Diagnostics/DiagnosticCenter.cs
- Arquivo grande (1030 linhas) — avaliar extração de helpers/serviços.
- 52 números mágicos — avaliar constantes nomeadas.
- 3 métodos >80 linhas (maior: 129).


## KitLugia.GUI/Pages/WindowsSettings/ForceStopUnlockPage.xaml.cs
- Arquivo grande (1059 linhas) — avaliar extração de helpers/serviços.
- 46 números mágicos — avaliar constantes nomeadas.
- 3 métodos >80 linhas (maior: 141).
- Process.Start — validar origem/argumentos (1 usos).


## KitLugia.GUI/Windows/TaskManager/KitTaskManager.Latency.cs
- Arquivo grande (836 linhas) — avaliar extração de helpers/serviços.
- 45 números mágicos — avaliar constantes nomeadas.
- 1 métodos >80 linhas (maior: 154).
- Process.Start — validar origem/argumentos (1 usos).


## KitLugia.Core/PathRepair.cs
- Arquivo grande (1000 linhas) — avaliar extração de helpers/serviços.
- 4 métodos >80 linhas (maior: 166).
- P/Invoke presente (2 declarações) — validar parâmetros nativos.


## KitLugia.Core/KitStore/StoreEngine.cs
- Arquivo grande (829 linhas) — avaliar extração de helpers/serviços.
- 29 números mágicos — avaliar constantes nomeadas.
- Process.Start — validar origem/argumentos (2 usos).


## KitLugia.GUI/Pages/WinTunePage.xaml.cs
- Arquivo grande (956 linhas) — avaliar extração de helpers/serviços.
- 1 métodos >80 linhas (maior: 99).


## KitLugia.GUI/Pages/WinpeToolsPage.xaml.cs
- Arquivo grande (1021 linhas) — avaliar extração de helpers/serviços.
- 55 números mágicos — avaliar constantes nomeadas.
- Process.Start — validar origem/argumentos (1 usos).


## KitLugia.Core/TaskManager/NativeHardware.cs
- Arquivo grande (767 linhas) — avaliar extração de helpers/serviços.
- 40 números mágicos — avaliar constantes nomeadas.
- 1 métodos >80 linhas (maior: 94).
- P/Invoke presente (17 declarações) — validar parâmetros nativos.


## KitLugia.Core/DriverManager.cs
- Arquivo grande (788 linhas) — avaliar extração de helpers/serviços.
- 2 métodos >80 linhas (maior: 188).
- 4 TODO/FIXME/HACK/XXX: [350, 500, 530, 537]
- Process.Start — validar origem/argumentos (2 usos).
- Directory.Delete — verificar guards (2 usos).


## KitLugia.Core/IsoManager.cs
- Arquivo grande (684 linhas) — avaliar extração de helpers/serviços.
- 25 números mágicos — avaliar constantes nomeadas.
- 3 métodos >80 linhas (maior: 136).
- Process.Start — validar origem/argumentos (14 usos).


## KitLugia.Core/OfflineInsiderManager.cs
- Arquivo grande (509 linhas) — avaliar extração de helpers/serviços.
- 34 números mágicos — avaliar constantes nomeadas.
- 1 métodos >80 linhas (maior: 107).
- Process.Start — validar origem/argumentos (2 usos).
- Directory.Delete — verificar guards (1 usos).


## KitLugia.GUI/Pages/SecurityPage.xaml.cs
- Arquivo grande (714 linhas) — avaliar extração de helpers/serviços.
