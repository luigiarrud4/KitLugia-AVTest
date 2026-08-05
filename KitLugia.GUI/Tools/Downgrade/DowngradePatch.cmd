@echo off
setlocal EnableExtensions EnableDelayedExpansion
title KitLugia - Downgrade de Build (patch setupcompat.dll)
rem ============================================================
rem  DowngradePatch.cmd - prepara a midia 25H2 para downgrade
rem  de build com preservacao de dados (patch setupcompat.dll).
rem
rem  Uso:
rem    DowngradePatch.cmd                  (modo interativo)
rem    DowngradePatch.cmd "C:\midia.iso"   (ISO via arg)
rem    DowngradePatch.cmd "C:\midia.iso" "C:\SAIDA"   (+ pasta saida)
rem    DowngradePatch.cmd "C:\midia.iso" "C:\SAIDA" noreg  (sem tocar registro)
rem ============================================================

echo.
echo  ==============================================================
echo   ATENCAO: TESTE EM VM PRIMEIRO antes de usar em maquina real.
echo   Metodo valida do (31/07/2026) em 25H2 26200.8973 por
echo   analise estatica (IDA). Upgrade install com auto-rollback e
echo   Windows.old (10 dias) como rede de seguranca - baixo risco,
echo   mas faca backup dos dados importantes antes.
echo  ==============================================================
echo.

rem ---- 7z ----
set "SCRIPT_DIR=%~dp0"
set "SEVENZ="
if exist "%SCRIPT_DIR%..\..\Resources\App\7Zip\7z.exe" set "SEVENZ=%SCRIPT_DIR%..\..\Resources\App\7Zip\7z.exe"
if not defined SEVENZ if exist "%ProgramFiles%\7-Zip\7z.exe" set "SEVENZ=%ProgramFiles%\7-Zip\7z.exe"
if not defined SEVENZ if exist "%ProgramFiles(x86)%\7-Zip\7z.exe" set "SEVENZ=%ProgramFiles(x86)%\7-Zip\7z.exe"
if not defined SEVENZ (
  echo [ERRO] 7z.exe nao encontrado - procurei no kit e em Program Files.
  pause
  exit /b 1
)

rem ---- ISO ----
set "ISO=%~1"
if not defined ISO (
  set "ISO="
  if exist "C:\uup" (
    for /f "delims=" %%i in ('dir /b /s /o:d "C:\uup\*.iso" 2^>nul') do set "ISO=%%i"
  )
  if defined ISO (
    echo ISO detectada: %ISO%
    call :ask "Usar esta ISO? [S/n]: "
    if /i "!ASKVAL!"=="n" set "ISO="
  )
  if not defined ISO (
    call :ask "Digite o caminho da ISO: "
    set "ISO=!ASKVAL!"
  )
)
if not exist "%ISO%" (
  echo [ERRO] ISO nao encontrada: %ISO%
  pause
  exit /b 1
)
echo [OK] ISO: %ISO%

rem ---- pasta de saida ----
set "OUT=%~2"
if not defined OUT (
  set "OUT=%USERPROFILE%\Downloads\ISO-26200.8973"
  call :ask "Pasta de extracao [%OUT%]: "
  if defined ASKVAL set "OUT=!ASKVAL!"
)

rem ---- extracao (pula se ja extraiu) ----
if exist "%OUT%\sources\setupcompat.dll" (
  echo [OK] Midia ja extraida em %OUT% - pulando extracao.
  goto :patch
)
echo Extraindo ISO em %OUT% ... (pode levar varios minutos)
"%SEVENZ%" x -y -o"%OUT%" "%ISO%"
if errorlevel 1 (
  echo [ERRO] Falha na extracao da ISO.
  pause
  exit /b 1
)

:patch
echo.
echo Aplicando patch na setupcompat.dll ...
powershell -NoProfile -ExecutionPolicy Bypass -File "%SCRIPT_DIR%patch_setupcompat.ps1" -Dll "%OUT%\sources\setupcompat.dll"
set "RC=%ERRORLEVEL%"
if "%RC%"=="0" goto :patch_ok
if "%RC%"=="1" (
  echo [ERRO] Padrao nao encontrado - esta build pode ter mudado o codigo.
  goto :fim
)
if "%RC%"=="2" (
  echo [ERRO] Padrao ambiguo - varias ocorrencias - NAO patcheado.
  goto :fim
)
if "%RC%"=="4" (
  echo [ERRO] setupcompat.dll nao encontrada em %OUT%\sources\
  goto :fim
)
echo [ERRO] Falha inesperada no patch (codigo %RC%).
goto :fim

:patch_ok
echo.

rem ---- registro Insider (opcional, requer admin) ----
if /i "%~3"=="noreg" (
  echo [SKIP] Remocao do registro Insider ignorada - argumento noreg.
  goto :final
)
net session >nul 2>&1
if errorlevel 1 (
  echo [AVISO] Sem direitos de admin: nao posso remover a inscricao Insider.
  echo          Rode como administrador depois e apague:
  echo          HKLM\SOFTWARE\Microsoft\WindowsSelfHost - faca backup antes
  goto :final
)
call :ask "Remover inscricao Insider (backup + delete HKLM\...\WindowsSelfHost)? [s/N]: "
if /i not "%ASKVAL%"=="s" (
  echo [SKIP] Registro Insider mantido.
  goto :final
)
reg export "HKLM\SOFTWARE\Microsoft\WindowsSelfHost" "%OUT%\WindowsSelfHost-backup.reg" /y >nul 2>&1
if exist "%OUT%\WindowsSelfHost-backup.reg" (
  echo [OK] Backup do registro: %OUT%\WindowsSelfHost-backup.reg
  reg delete "HKLM\SOFTWARE\Microsoft\WindowsSelfHost" /f
  if errorlevel 1 (
    echo [AVISO] Falha ao apagar a chave do registro - apague manualmente.
  ) else (
    echo [OK] Inscricao Insider removida.
  )
) else (
  echo [AVISO] Chave WindowsSelfHost nao encontrada ou sem backup - nada a remover.
)

:final
echo.
echo Gerando hash SHA256 da DLL patcheada ...
for /f "delims=" %%h in ('powershell -NoProfile -Command "(Get-FileHash -Algorithm SHA256 -LiteralPath '%OUT%\sources\setupcompat.dll').Hash"') do set "SHA256=%%h"
echo SHA256: %SHA256%
echo (salvo em %OUT%\setupcompat.sha256)
echo %SHA256% > "%OUT%\setupcompat.sha256"
echo.
echo ==============================================================
echo  PRONTO. Proximos passos:
echo   1. Abra: %OUT%
echo   2. Rode o setup.exe da PASTA (nao da ISO montada):
echo      - duplo clique e escolha "Keep personal files and apps"
echo        (deve estar HABILITADO por causa do patch)
echo      - ou: setup.exe /auto upgrade /quiet /noreboot
echo   3. Apos o downgrade, limpe KBs residuais do Insider no kit
echo      (card "Controle de Updates" - remover KB).
echo ==============================================================
echo.
call :ask "Abrir a pasta agora? [s/N]: "
if /i "%ASKVAL%"=="s" explorer "%OUT%"
goto :fim

rem ---- sub-rotina: prompt via Read-Host (le 1 linha, funciona
rem       com console, pipe ou arquivo; EOF retorna vazio) ----
rem       prompt vai ao console via stderr; valor capturado via stdout
:ask
set "ASKVAL="
for /f "delims=" %%i in ('powershell -NoProfile -ExecutionPolicy Bypass -Command "[Console]::Error.Write('%~1 '); Read-Host"') do set "ASKVAL=%%i"
exit /b

:fim
echo.
pause
exit /b 0
