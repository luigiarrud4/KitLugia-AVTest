@echo off
chcp 65001 >nul
cd /d "%~dp0"

echo ===== KitLugia Deploy =====

:: Pede a versao (Enter = manter ultima)
set /p VERSION="Digite a versao (Enter = ultima, ex: 2.0.21): "
if "%VERSION%"=="" (
    for /f "tokens=*" %%a in ('gh release list --repo luigiarrud4/KitLugia-AVTest --limit 1 --json tagName --jq ".[0].tagName"') do set "TAG=%%a"
    set "VERSION=%TAG:v=%"
    if "%VERSION%"=="" (
        echo ERRO: Nao foi possivel obter ultima versao do GitHub.
        pause
        exit /b 1
    )
    echo Usando ultima versao: %VERSION%
)

:: Verifica gh auth
echo.
echo [0/5] Verificando autenticacao...
gh auth status >nul 2>&1
if %errorlevel% neq 0 (
    echo gh nao autenticado. Abrindo navegador para login...
    gh auth login -h github.com -w
    if %errorlevel% neq 0 (
        echo ERRO: Falha ao autenticar gh. Tente manualmente: gh auth login -h github.com -w
        pause
        exit /b 1
    )
)

echo.
echo [1/5] Build + ZIP + SHA256 (versao %VERSION%)...
powershell -ExecutionPolicy Bypass -File "Deploy.ps1" -Version "%VERSION%"
if %errorlevel% neq 0 (
    echo ERRO no Deploy.ps1
    pause
    exit /b 1
)

echo.
echo [2/5] Criando release v%VERSION% e upload assets...
gh release create "v%VERSION%" --title "KitLugia v%VERSION%" --notes "Release automatica v%VERSION%" ./Publish/KITLUGIA2.zip ./Publish/KITLUGIA2.zip.sha256
if %errorlevel% neq 0 (
    echo AVISO: Criacao/upload falhou. Faca manualmente em:
    echo   github.com/luigiarrud4/KitLugia-AVTest/releases/new?tag=v%VERSION%
)

echo.
echo [3/5] Git add + commit...
git add -A
if %errorlevel% neq 0 goto :push

set /p msg="Digite a mensagem do commit (ou Enter para padrao): "
if "%msg%"=="" set "msg=Deploy v%VERSION%"
git commit -m "%msg%"

:push
echo.
echo [4/5] Git push...
git push

echo.
echo [5/5] Tag v%VERSION%...
git tag "v%VERSION%"
git push origin "v%VERSION%"

echo.
echo ===== Pronto! KitLugia v%VERSION% publicado =====
echo.
echo Teste: abra o kit na versao anterior e clique em Atualizar.
pause
