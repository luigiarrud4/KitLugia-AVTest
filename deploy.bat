@echo off
chcp 65001 >nul
cd /d "%~dp0"

echo ===== KitLugia Deploy =====

:: Verifica gh auth - se nao estiver logado, abre o navegador
echo.
echo [0/4] Verificando autenticacao...
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
echo [1/4] Build + ZIP + SHA256...
powershell -ExecutionPolicy Bypass -File "Deploy.ps1"
if %errorlevel% neq 0 (
    echo ERRO no Deploy.ps1
    pause
    exit /b 1
)

echo.
echo [2/4] Upload assets para GitHub...
gh release upload v2.0.20 ./Publish/KITLUGIA2.zip ./Publish/KITLUGIA2.zip.sha256 --clobber
if %errorlevel% neq 0 (
    echo AVISO: Upload falhou. Faca manualmente em:
    echo   github.com/luigiarrud4/KitLugia-AVTest/releases
)

echo.
echo [3/4] Git add + commit...
git add -A
if %errorlevel% neq 0 goto :push

set /p msg="Digite a mensagem do commit (ou Enter para padrao): "
if "%msg%"=="" set "msg=Deploy update"
git commit -m "%msg%"

:push
echo.
echo [4/4] Git push...
git push

echo.
echo ===== Pronto! =====
pause
