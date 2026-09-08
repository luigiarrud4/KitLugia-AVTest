@echo off
title KitLugia - Restaurar Icones do Desktop
setlocal
set "TOOL=%~dp0DesktopIconLayout.ps1"
set "BACKUP_DIR=%LOCALAPPDATA%\KitLugia\IconLayoutBackup"

rem Estrutura com goto (sem blocos if/for com expansao de caminho dentro):
rem o path do projeto contem "(25)" e um parentese fechando dentro de bloco
rem quebra o parse do cmd ("- foi inesperado neste momento").

if exist "%TOOL%" goto :tool_ok
echo [ERRO] DesktopIconLayout.ps1 nao encontrado ao lado deste .bat:
echo   %TOOL%
pause
exit /b 1
:tool_ok

rem Alvo: arquivo .json arrastado sobre este .bat OU o backup mais recente da pasta
set "JSON=%~1"
if defined JSON goto :json_ok
rem Auto-pick SOMENTE backups completos; pre_restore_* fica para undo manual (arrastar)
for /f "delims=" %%F in ('dir /b /o-d "%BACKUP_DIR%\desktop_layout_*.json" 2^>nul') do (
  if not defined JSON set "JSON=%BACKUP_DIR%\%%F"
)
:json_ok
if defined JSON goto :have_json
echo [ERRO] Nenhum backup .json encontrado em:
echo   %BACKUP_DIR%
echo Faca um backup primeiro ou arraste um .json sobre este .bat.
pause
exit /b 1
:have_json

echo.
echo === KitLugia - Restaurar Icones do Desktop ===
echo Backup selecionado:
echo   %JSON%
echo.

rem Snapshot de seguranca do estado ATUAL antes de restaurar, para undo
for /f %%T in ('powershell -NoProfile -Command "Get-Date -Format yyyyMMdd_HHmmss"') do set "TS=%%T"
set "PRE=%BACKUP_DIR%\pre_restore_%TS%.json"
echo Salvando snapshot do estado atual para undo:
echo   %PRE%
powershell -NoProfile -ExecutionPolicy Bypass -File "%TOOL%" -OutFile "%PRE%"
echo.

echo Restaurando posicoes...
powershell -NoProfile -ExecutionPolicy Bypass -File "%TOOL%" -RestoreFile "%JSON%"
echo.
echo Pronto. Para desfazer, arraste o snapshot pre_restore mais recente
echo sobre este .bat. Para restaurar outro backup, arraste o .json dele.
echo.
pause
