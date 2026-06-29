# Batch Updater — KitLugia v2.0.30+

## Motivação

O updater anterior usava um executável separado (`KitLugia.Updater.exe`) publicado com `PublishSingleFile=true` e `SelfContained=false`. Esse EXE era embutido como recurso no `KitLugia.Core.dll`, extraído em tempo de execução e executado via `Process.Start`.

**Problemas identificados:**

1. O EXE extraído nunca aparecia — o processo filho falhava silenciosamente ao iniciar (provavelmente incompatibilidade do host do .NET com assemblies WPF extraídos de recurso embutido)
2. Adicionava ~200 KB ao ZIP (pouco, mas desnecessário)
3. Dependência de framework .NET runtime disponível para o filho
4. Complexidade de build: precisava publicar o updater antes do Core, copiar para Resources, etc.

## Solução: Script Batch Auto-deletável

Substituiu-se o EXE separado por um **script `.cmd` gerado em tempo real** pelo próprio GUI. Zero KB adicional no ZIP, zero dependências externas, zero falha de inicialização.

### Fluxo

```
GUI (UpdatePage)                  Script (.cmd)                  Sistema
      │                               │                            │
      ├─ Baixa ZIP do GitHub ─────────┤                            │
      ├─ Gera KitLugia_Update.cmd ────┤                            │
      ├─ Process.Start(.cmd) ─────────┤                            │
      ├─ Shutdown() ──────────────────┤                            │
      │                               ├─ tasklist /PID wait ───────┤
      │                               ├─ PowerShell Expand-Archive │
      │                               ├─ Limpa temporários ───────┤
      │                               ├─ Escreve UPDATE_COMPLETE ──┤
      │                               ├─ start KitLugia.GUI.exe ──┤
      │                               ├─ timeout 3s ──────────────┤
      │                               └─ Self-delete (del %~f0) ──┤
```

### O Script Gerado

O método `GitHubUpdater.GenerateUpdateBatch()` em `GitHubUpdater.cs:317` gera o seguinte script:

```batch
@echo off
title KitLugia - Atualizando...
color 0E
cls
echo ================================================
echo          KIT LUGIA - ATUALIZACAO
echo ================================================
echo.
echo [1/4] Aguardando fechamento do KitLugia...
:wait
tasklist /fi "PID eq <PID>" 2>nul | findstr /i "<PID>" >nul
if not errorlevel 1 (
    timeout /t 1 /nobreak >nul
    goto wait
)
echo  OK - KitLugia fechado.
echo.
echo [2/4] Extraindo arquivos...
powershell -NoProfile -ExecutionPolicy Bypass -Command "try { Expand-Archive -Path '<ZIP>' -DestinationPath '<APPDIR>' -Force; exit 0 } catch { echo $_; pause; exit 1 }"
if errorlevel 1 (
    echo  ERRO - Falha ao extrair arquivos.
    pause
    exit /b 1
)
echo  OK - Arquivos extraidos.
echo.
echo [3/4] Limpando temporarios...
if exist "%~dp0KitLugia.Updater.exe" del /q "%~dp0KitLugia.Updater.exe" 2>nul
if exist "%~dp0KitLugia.Updater.dll" del /q "%~dp0KitLugia.Updater.dll" 2>nul
if exist "%~dp0update.log" del /q "%~dp0update.log" 2>nul
echo  OK - Temporarios removidos.
echo.
echo {"OldVersion":"<OLD>","NewVersion":"<NEW>"} > "%~dp0UPDATE_COMPLETE.txt"
echo [4/4] Iniciando nova versao...
start "" "%~dp0KitLugia.GUI.exe"
echo.
echo ================================================
echo     ATUALIZACAO CONCLUIDA!
echo ================================================
timeout /t 3 /nobreak >nul
del "%~f0"
```

**Destaques:**
- `tasklist /fi "PID eq ..."` — aguarda até 1s entre verificações, sem timeout máximo
- `PowerShell Expand-Archive` — extração confiável de ZIP, disponível em todo Windows 10+
- `UPDATE_COMPLETE.txt` — JSON com versões, lido pelo GUI na inicialização para mostrar notificação
- `del "%~f0"` — auto-deleção ao finalizar, sem deixar rastros

### Localização do Script

O script é escrito no **mesmo diretório do executável do GUI** (`appDir`), pois:
- Precisa extrair o ZIP sobrepondo os arquivos existentes naquela pasta
- Precisa reiniciar o `KitLugia.GUI.exe` do mesmo local
- Tem permissão de escrita (o usuário está executando de lá)

### Notificação de Atualização

O GUI lê `UPDATE_COMPLETE.txt` na inicialização (`CheckForUpdateNotificationAsync`) e exibe um `UpdateNotification` com:
- Versão antiga → versão nova (ex: `2.0.29 → 2.0.30`)
- Label "Reinstalado" se `OldVersion == NewVersion`
- Botão "ENTENDIDO" para dismiss
- Aparece apenas uma vez por atualização (arquivo é deletado após leitura)

## Arquivos Modificados

| Arquivo | Mudança |
|---|---|
| `KitLugia.Core/GitHubUpdater.cs` | Substitui extração/execução do EXE por `GenerateUpdateBatch()` |
| `KitLugia.GUI/Pages/UpdatePage.xaml.cs` | Mesma substituição |
| `KitLugia.Core/KitLugia.Core.csproj` | Remove `EmbeddedResource` do `KitLugia.Updater.exe` e `ProjectReference` |
| `KitLugia.sln` | Remove projeto KitLugia.Updater |
| `Deploy.ps1` | Remove passo de publish do updater; steps passam de 1/5→1/4 |

## Antivirus

Testado com Windows Defender — **nenhum alerta**. O script `.cmd` gerado é simples (tasklist, powershell, start, del) e não contém ofuscação ou comportamentos suspeitos.

## Possível Evolução

Se no futuro houver problemas com AV:
- Migrar para PowerShell script (`.ps1`) — menos propenso a falsos positivos
- Ou usar abordagem de auto-instância: GUI relança a si mesmo com argumento `--apply-update` e o PID a aguardar
