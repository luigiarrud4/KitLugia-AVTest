@echo off
chcp 65001 >nul
cd /d "%~dp0"

if exist "C:\Program Files\Git\cmd\git.exe" set "PATH=C:\Program Files\Git\cmd;C:\Program Files\Git\bin;%PATH%"
if exist "C:\Program Files\GitHub CLI\gh.exe" set "PATH=C:\Program Files\GitHub CLI;%PATH%"

echo.
echo  ╔══════════════════════════════════════════════════╗
echo  ║          KITLUGIA DEPLOY                        ║
echo  ╚══════════════════════════════════════════════════╝
echo.

:: 1. Obter versao (PowerShell escreve no arquivo, batch le)
echo  [1/6] Consultando versao no GitHub...
echo.

set "VFILE=%TEMP%\kl_deploy_ver.txt"
del "%VFILE%" 2>nul

powershell -NoProfile -Command "$r=Invoke-RestMethod 'https://api.github.com/repos/luigiarrud4/KitLugia-AVTest/releases/latest' -UseBasicParsing -TimeoutSec 10; $v=$r.tag_name -replace '^v',''; $p=$v.Split('.'); $p[2]=[int]$p[2]+1; [IO.File]::WriteAllText('%VFILE%',('{0}.{1}.{2}' -f $p[0],$p[1],$p[2]))" >nul 2>&1

set "NEXT="
if exist "%VFILE%" set /p "NEXT=<%VFILE%"
if exist "%VFILE%" del "%VFILE%"

if not defined NEXT (
    echo  Nao foi possivel obter a versao. Informe manualmente.
    echo.
    set /p "VER=  Versao: "
    goto :check_ver
)

echo  ┌─────────────────────────────────────────────────┐
echo  │  Proxima versao: v%NEXT%
echo  └─────────────────────────────────────────────────┘
echo.
echo  ENTER = publicar v%NEXT%
echo  Ou digite outra versao (ex: 2.1.0)
echo.
set /p "VER=  Versao: "
if "%VER%"=="" set "VER=%NEXT%"

:check_ver
if not defined VER (
    echo  ERRO: Versao obrigatoria.
    pause
    exit /b 1
)

echo.
echo  Publicando v%VER%...
echo.

:: 2. Auth
echo  [2/6] Autenticacao...
gh auth status >nul 2>&1
if %errorlevel% neq 0 (
    gh auth login -h github.com -w
    if %errorlevel% neq 0 (
        echo  ERRO: auth falhou
        pause
        exit /b 1
    )
)
echo  OK
echo.

:: 3. Build
echo  [3/6] Build + ZIP + SHA256...
powershell -ExecutionPolicy Bypass -File "Deploy.ps1" -Version "%VER%"
if %errorlevel% neq 0 (
    echo  ERRO no Deploy.ps1
    pause
    exit /b 1
)
echo.

:: 4. Release
echo  [4/6] Criando release v%VER%...
gh release create "v%VER%" --title "KitLugia v%VER%" --notes "Release automatica v%VER%" ./Publish/KITLUGIA2.zip ./Publish/KITLUGIA2.zip.sha256
if %errorlevel% neq 0 echo  AVISO: release falhou, faca manualmente
echo.

:: 5. Commit
echo  [5/6] Git commit...
git add -A
set /p "MSG=  Mensagem (Enter = padrao): "
if "%MSG%"=="" set "MSG=Deploy v%VER%"
git commit -m "%MSG%"

:: 6. Push
echo.
echo  [6/6] Git push...
git push --set-upstream origin main 2>&1

git tag -f "v%VER%"
git push origin "v%VER%" --force 2>&1
if %errorlevel% neq 0 (
    git push origin --delete "v%VER%" 2>&1
    git push origin "v%VER%" 2>&1
)

echo.
echo  ╔══════════════════════════════════════════════════╗
echo  ║  KITLUGIA v%VER% PUBLICADO!
echo  ╚══════════════════════════════════════════════════╝
echo.
pause
