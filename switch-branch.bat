@echo off
chcp 65001 >nul
setlocal EnableDelayedExpansion
title KitLugia - Troca Rapida de Branch Git
color 0A

cd /d "%~dp0"
if not exist ".git" (
    echo ERRO: execute este .bat dentro da pasta do repositorio KitLugia.
    pause
    exit /b 1
)

:menu
cls
echo ============================================
echo   KITLUGIA - Troca Rapida de Branch Git
echo ============================================
echo.
echo Pasta: %CD%
echo.
echo Branch atual:
git branch --show-current
echo.
echo --------------------------------------------
echo  1 - Trocar de branch por numero (Rapido)
echo  2 - Listar stashes salvos
echo  3 - Criar nova branch a partir da atual
echo  4 - Sair
echo --------------------------------------------
set "opt="
set /p opt=Escolha: 

if "%opt%"=="1" goto switch_numbered
if "%opt%"=="2" goto stashlist
if "%opt%"=="3" goto newbranch
if "%opt%"=="4" goto end
echo Opcao invalida.
pause
goto menu

:switch_numbered
echo.
echo --------------------------------------------
echo  SELECIONE A BRANCH DE DESTINO:
echo --------------------------------------------

:: Limpa mapeamentos antigos
for /f "tokens=1 delims==" %%i in ('set b_ 2^>nul') do set "%%i="

set "count=0"
for /f "tokens=*" %%b in ('git branch --format="%%(refname:short)"') do (
    set /a count+=1
    set "b_!count!=%%b"
    echo   [!count!] %%b
)

echo --------------------------------------------
set "num="
set /p num=Digite o numero da branch desejada (ou ENTER para cancelar): 
if "%num%"=="" goto menu

set "target="
for /f "tokens=2 delims==" %%v in ('set b_%num% 2^>nul') do set "target=%%v"

if "%target%"=="" (
    echo.
    echo Numero invalido.
    pause
    goto menu
)

for /f "tokens=*" %%c in ('git branch --show-current') do set "current_branch=%%c"
if /i "%target%"=="%current_branch%" (
    echo.
    echo Voce ja esta na branch "%target%".
    pause
    goto menu
)

echo.
:: Verifica se ha alteracoes pendentes antes de fazer o stash
set "has_changes=0"
git diff --quiet 2>nul || set "has_changes=1"
git diff --cached --quiet 2>nul || set "has_changes=1"

if "%has_changes%"=="1" (
    echo Salvando alteracoes atuais temporariamente...
    git stash push -u -m "auto-stash-quick-switch" >nul 2>&1
) else (
    echo Nenhum arquivo modificado. Pulando etapa de salvamento...
)

echo Alternando para a branch "%target%"...
git checkout "%target%"
if errorlevel 1 (
    echo.
    echo ERRO ao trocar de branch.
    if "%has_changes%"=="1" git stash pop >nul 2>&1
    pause
    goto menu
)

if "%has_changes%"=="1" (
    echo Restaurando seu trabalho na nova branch...
    git stash pop >nul 2>&1
)

echo.
echo ============================================
echo Sucesso! Agora voce esta na branch:
git branch --show-current
echo ============================================
echo.
pause
goto menu

:stashlist
echo.
echo Stashes disponiveis:
git stash list
echo.
pause
goto menu

:newbranch
echo.
set "name="
set /p name=Nome da nova branch: 
if "%name%"=="" goto menu
git checkout -b "%name%"
if errorlevel 1 ( echo ERRO ao criar branch. ) else ( echo Branch "%name%" criada com sucesso. )
pause
goto menu

:end
echo.
echo Fim.
pause
endlocal
exit /b 0