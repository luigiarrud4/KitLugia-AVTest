# MultiISO Boot Architecture (UEFI)

## Problema

Windows Boot Manager (WBM) **não consegue chainload** binarios EFI
nao-Windows (GRUB/shim) via BCD. Erro 0xc000007b:

> "Microsoft has blocked the loading of legacy or non-Windows
> operating systems from the BCD menu" — EasyBCD docs

## Solucao: NVRAM BootSequence (BootNext)

Em vez de chainload, o KitLugia usa o recurso `BootNext` da
firmware UEFI, equivalente ao `efibootmgr --bootnext` no Linux.

### Fluxo

1. `bcdedit /copy {bootmgr} /d "Linux (...)"`
   Clona a entrada do Windows Boot Manager (tipo boot manager,
   NAO osloader).

2. `bcdedit /set {guid} device partition=X:`
   `bcdedit /set {guid} path \EFI\...\grubx64.efi`
   Aponta para o bootloader EFI do Linux na particao.

3. `bcdedit /displayorder {guid} /addlast`
   Adiciona ao menu do WBM (fallback visual apenas).

4. `bcdedit /set {fwbootmgr} bootsequence {guid}`
   Escreve BootNext na NVRAM da UEFI — saida do WBM.

### No reboot

1. Firmware le BootNext na NVRAM
2. Pula WBM completamente
3. Carrega direto o bootloader do Linux (GRUB/shim)
4. BootNext e consumido/resetado (one-time)

### Vantagem

Sem chainload pelo WBM = sem 0xc000007b. O Linux recebe
controle direto do firmware, com acesso correto aos devices.

### Codigo fonte

`KitLugia.Core\WinbootManager.cs` — metodo
`CreateDirectNvramBoot()` (~linha 2398)

### Fallback (Legacy BIOS)

`CreateLegacyBootEntry()` cria entrada BCD tipo bootsector
apontando para isolinux.bin/ldlinux.sys. Em BIOS nao ha
NVRAM via bcdedit, entao depende do menu BCD classico.
