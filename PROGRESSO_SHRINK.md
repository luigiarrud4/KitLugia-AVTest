# Progresso do Shrink via Emergency Pre-Boot

## Problema Original
Shrink de partição exige boot externo (fora do Windows). O Windows não permite redimensionar partição montada.

## Abordagens Testadas

### 1. BCD `osloader` apontando para kernel Linux ❌
**Problema**: `bcdedit /create /application osloader` + `/bootsequence {guid}` → erro `0xc000007b` (STATUS_INVALID_IMAGE_FORMAT).  
**Causa**: Windows Boot Manager valida que o binário destino é PE32+ assinado pela Microsoft. Kernel Linux com EFI stub é rejeitado.

### 2. BCD `/displayorder {guid}` ❌
**Tentativa**: Usar `/displayorder {guid} /addfirst` em vez de `/bootsequence`, esperando que a UEFI firmware carregasse o binário diretamente.  
**Resultado**: Mesmo erro `0xc000007b`. O Windows Boot Manager ainda processa entradas do firmware boot manager.

### 3. rEFInd via file hijack ✅ (CORRENTE - FUNCIONA)
**Método**: Substituir o arquivo `bootmgfw.efi` no ESP pelo rEFInd.  
**Por que funciona**: A UEFI firmware carrega `\EFI\Microsoft\Boot\bootmgfw.efi` como sempre — agora o arquivo **é** o rEFInd. A firmware carrega como EFI genérico sem validação.

**Implementação em**: `KitLugia.Core/EmergencyBcdBootManager.cs`

---

## Cadeia de Boot Final
```
App GUI (ShrinkPage)
  → EmergencyBcdBootManager.DeployAsync()
    → Monta ESP (mountvol X: /S)
    → Cria pasta \EFI\KitLugia\ no ESP
    → Backup: bootmgfw.efi → \EFI\KitLugia\bootmgfw.original.efi
    → COPIA refind_x64.efi → \EFI\Microsoft\Boot\bootmgfw.efi (OVERWRITE!)
    → Escreve refind.conf em \EFI\Microsoft\Boot\refind.conf
      timeout 0
      default_selection KitLugia
      menuentry "KitLugia Alpine Recovery" {
          volume "ESP"
          loader /EFI/KitLugia/vmlinuz
          options "initrd=/EFI/KitLugia/initrd.gz kitlugia_disk=..."
      }
      menuentry "Windows" {
          loader /EFI/KitLugia/bootmgfw.original.efi
          ostype Windows
      }
    → Copia vmlinuz + initrd.gz para \EFI\KitLugia\
    → kernel_params.txt (fallback)
    → REBOOT
      → UEFI firmware → \EFI\Microsoft\Boot\bootmgfw.efi (agora rEFInd)
        → timeout 0 → Alpine (vmlinuz + initrd.gz)
          → ntfsresize + sfdisk
          → Escreve preboot_complete.txt em \EFI\KitLugia\
          → REBOOT
            → Windows carrega → MainWindow.CheckShrinkCompletionAsync()
              → CleanupAsync() restaura bootmgfw.efi do backup
              → Deleta \EFI\KitLugia\
              → Deleta refind.conf
```

---

## Arquivos Modificados/Criados

### `KitLugia.Core/EmergencyBcdBootManager.cs`
- Classe completa de deploy + cleanup
- **Método principal**: `DeployAsync()` — hijack do bootmgfw.efi, agora aceita `linuxFlavor` ("alpine" ou "antix")
- **LinuxFlavor**: propriedade estática que altera o diretório de kernel/initrd para `Resources/BootGoodies/{flavor}`
- **refind.conf dinâmico**: gera texto do menu conforme o sabor escolhido (Alpine ou antiX)
- **CleanupAsync()** — restaura boot original do backup
- **IsPreBootCompleted()** — detecta marcador pós-reboot
- **TriggerReboot()** — shutdown com mensagem
- **MountEspAsync()** — monta ESP com mountvol
- **MountEspSync()** — versão síncrona (usada pelos botões)
- Removeu toda dependência de BCD (bcdedit)
- initrd movido para dentro de `options` no refind.conf (o rEFInd com initrd separado causava parse errado com `\EF\I KitLugia`)

### `KitLugia.GUI/Pages/ShrinkPage.xaml`
- Botão "Executar Shrink" (gold)
- **Novo**: RadioButtons "Alpine (8MB, init mínimo)" e "antiX (21MB, kernel completo)"
- **Novo**: `TxtFlavorInfo` — texto descritivo que muda conforme o rádio selecionado
- **Novo**: Botão "Instalar rEFInd" (azul/RecycleButtonStyle)
- **Novo**: Botão "Desinstalar rEFInd" (vermelho/DeleteButtonStyle)

### `KitLugia.GUI/Pages/ShrinkPage.xaml.cs`
- `GetSelectedLinuxFlavor()`: retorna "antix" ou "alpine" baseado no RadioButton
- BtnExecute_Click: passa `linuxFlavor` para DeployAsync
- `RadioFlavor_Changed`: atualiza `TxtFlavorInfo` com descrição do sabor
- BtnInstallRefind_Click: monta ESP, faz backup, substitui bootmgfw.efi
- BtnRemoveRefind_Click: chama CleanupAsync() do EmergencyBcdBootManager
- Removeu métodos obsoletos: SaveBcdGuid, LoadBcdGuid, ClearBcdGuid

### `KitLugia.GUI/MainWindow.xaml.cs`
- CheckShrinkCompletionAsync() simplificado: não usa mais BcdGuid
- CleanupAsync() sem parâmetro de GUID

### `KitLugia.GUI/Themes/Buttons.xaml`
- Novo estilo: `RecycleButtonStyle` (azul #2196F3)
- Novo estilo: `DeleteButtonStyle` (vermelho #E53935)

### `KitLugia.Core/Resources/BootGoodies/antix/` (NOVO)
- `vmlinuz` (10.3MB) — kernel antiX-26 x64 (Ubuntu-based, drivers SATA/NVMe/SCSI built-in)
- `initrd.gz` (11.6MB) — initramfs antiX original (busybox completo, shell, procura linuxfs)

---

## Problemas Atuais

### 1. Alpine: Kernel não encontra discos - E initrd não é carregado
**Sintoma**: Kernel panic "VFS: Unable to mount root fs on unknown-block(0,0)" após o rEFInd iniciar o kernel.  
**Causa raiz**: O EFI stub do kernel não está carregando o initrd.gz. O rEFInd mostra `Using load options 'initrd=EFI\KitLugia\initrd.gz'` — o path perdeu a barra inicial (`/`) e as barras foram invertidas (`\`).  
**Dois problemas combinados**:
1. O path do initrd no `options` do refind.conf está sendo mal interpretado pelo rEFInd/EFI stub
2. Mesmo que carregasse, o Alpine initramfs não tem módulos de kernel para drivers de disco (SATA, NVMe, PVSCSI, etc.)  
**Status**: Alpine é a opção legada. antiX é a aposta principal agora.

### 2. antiX: initrd.gz original espera linuxfs (squashfs 585MB)
**Sintoma**: O init padrão do antiX procura `/live/linuxfs` na ISO ou em disco. Sem o linuxfs, cai no shell (live fail).  
**Impacto**: Para boot automático sem intervenção, o shrink precisa ser feito manualmente via shell. O initrd.gz original também é 11.6MB (cabe no ESP).
**Mitigação**: Usar `from=hd` nos kernel params para procurar linuxfs em disco rígido. Solução futura: rebuild do initrd.gz antiX com init script customizado (como o Alpine) que contém ntfsresize/sfdisk nativamente.

### 3. pós-reboot sem marcador
**Sintoma**: Durante testes em VM, VM crashou com "CPU has been disabled" (VMware bug com reboot ACPI de Linux minimalista).  
**Impacto**: preboot_complete.txt não foi escrito, então CleanupAsync() não restaura boot original.  
**Mitigação**: Se o usuário reiniciar manualmente, a detecção pós-reboot no MainWindow tentará cleanup.

---

## Como Construir o Alpine initramfs
```sh
# Em Linux (WSL, Docker ou máquina real):
cd KitLugia.Core/Resources/BootGoodies/alpine/
bash build.sh
# Gera: vmlinuz + initrd.gz
```

## Como Testar
1. Abrir KitLugia
2. Aba **Shrink**
3. Selecionar partição e tamanho
4. Clicar **Executar Shrink**
5. Confirmar → KitLugia substitui bootmgfw.efi, copia Alpine, reinicia
6. rEFInd aparece → timeout 0 → Alpine executa shrink
7. Se funcionar: preboot_complete.txt é escrito → reboot → KitLugia restaura boot original

---

## Tags
`shrink` `efi` `bootloader` `hijack` `refind` `bcd` `alpine` `ntfsresize` `sfdisk`
