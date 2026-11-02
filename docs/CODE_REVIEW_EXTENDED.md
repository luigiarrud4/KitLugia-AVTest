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
