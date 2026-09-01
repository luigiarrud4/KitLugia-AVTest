@echo off
chcp 65001 >nul
setlocal

set "VFILE=%TEMP%\kl_deploy_ver.txt"
del "%VFILE%" 2>nul

set "SCRIPTPATH=%~dp0kl_deploy_get_version.ps1"

powershell -NoProfile -ExecutionPolicy Bypass -File "%SCRIPTPATH%" >nul 2>&1

set "NEXT="
if exist "%VFILE%" set /p "NEXT=<%VFILE%"
if exist "%VFILE%" del "%VFILE%"

if not defined NEXT (
    echo  ERRO: Nao foi possivel obter a versao no GitHub.
    echo  Causas comuns: sem internet, repo privado, limite de API, ou sem releases.
    echo.
    if not exist "%SCRIPTPATH%" (
        echo  Solucao: o script kl_deploy_get_version.ps1 nao foi encontrado em "%SCRIPTPATH%"
    ) else (
        echo  Solucao: crie uma release com tag vX.Y.Z no repo luigiarrud4/KitLugia-AVTest
        echo          e execute este script novamente.
    )
    echo.
    pause
    exit /b 1
)

 echo  Proxima versao: v%NEXT%

endlocal