# KitLugia — AGENTS.md

## Estado atual do projeto (29/07/2026)

### O que foi feito

1. **SCHEDULE refatorado**: usa `UpdateWimWithScriptAsync` (wimlib, comando `--command` único) + `InjectConfigIntoWimAsync` (agora tenta wimlib primeiro). Fallback DISM aceita `scriptName` para não sobrescrever bridge.

2. **diskpart.exe injetado no WIM VALOS**: `InjectDiskpartIntoWimAsync` copia do host via wimlib (VALOS base não inclui).

3. **Winlogon Shell + SYSTEM\Setup\CmdLine configurados** no registro offline do VALOS via `ConfigureValosShellAsync`:
   - `HKLM\...\Winlogon\Shell = cmd /k C:\Windows\System32\startnet.valos.cmd`
   - `HKLM\SYSTEM\Setup\CmdLine = cmd /k C:\Windows\System32\startnet.valos.cmd` (fallback)
   - O log mostra o valor **atual** do `Setup\CmdLine` antes de sobrescrever

4. **Bridge startnet.cmd corrigida**: agora checa tanto `X:\` (WinPE) quanto `C:\` (VALOS) para `startnet.valos.cmd`.

5. **WinXShell injetável**: `InjectWinXShellIntoWimAsync` + `ResolveWinXShellAsync`:
   - Procura localmente em `KitLugia.WinPE\WinXShell\WinXShell.exe`
   - Fallback: download de `https://github.com/luigiarrud4/KitLugia-WinPE/releases/download/v1.0/WinXShell.exe`
   - Injeta no WIM via wimlib em `C:\Windows\System32\WinXShell.exe`

6. **Script VALOS modificado**: se `WinXShell.exe` estiver presente no WIM e não houver shrink pendente, lança WinXShell como GUI automaticamente.

7. **/Optimize removido** de todas as 8 chamadas DISM.

8. **Condição `!DISK_N!` removida** do RamdiskStartnetCmd (DISK_N=0 é válido).

### Fluxo de uso

**SCHEDULE (shrink automático)**:
- PREPARE cria WIM com script + bridge + registro
- SCHEDULE injeta shrink_config.ini + script atualizado
- VALOS boota, shrink roda, reboot
- WinXShell NÃO é necessário

**TESTAR (modo GUI)**:
- Clica TESTAR → injeta WinXShell no WIM
- VALOS boota com WinXShell como interface
- Útil para debug/inspeção manual

### Problema atual

VALOS ainda pode bootar direto para cmd.exe se o registro não for respeitado. As configurações duais (Winlogon Shell + Setup\CmdLine + bridge fix) devem cobrir todos os mecanismos de boot. Se ainda falhar, o WIM pode ter mecanismo próprio (winpeshl.ini, etc.).

### Próximos passos (pendentes)

- [ ] Testar PREPARE + SCHEDULE → reboot → shrink
- [ ] Testar WinXShell injection → boot VALOS com GUI
- [ ] Se ainda falhar: diagnosticar WIM (verificar winlogon.exe, winpeshl.exe, winpeshl.ini, registry)

### Arquivos modificados

- `KitLugia.Core\WinpeBuilder.cs`: ConfigureValosShellAsync (dual registry + log), InjectConfigIntoWimAsync (wimlib fallback), InjectDiskpartIntoWimAsync, InjectBootFilesIntoWimAsync (scriptName), InjectWinXShellIntoWimAsync, ResolveWinXShellAsync, /Optimize removido
- `KitLugia.Core\WinbootManager.cs`: RamdiskStartnetCmd, ValidationOsStartnetCmd (WinXShell launch), bridge startnet.cmd (C:\ check), chamada ConfigureValosShellAsync em PREPARE
