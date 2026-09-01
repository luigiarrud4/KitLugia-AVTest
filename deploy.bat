@echo off
chcp 65001 >nul 2>&1
setlocal EnableDelayedExpansion
cd /d "%~dp0"

if exist "C:\Program Files\Git\cmd\git.exe" set "PATH=C:\Program Files\Git\cmd;C:\Program Files\Git\bin;%PATH%"
if exist "C:\Program Files\GitHub CLI\gh.exe" set "PATH=C:\Program Files\GitHub CLI;%PATH%"

echo.
echo  ===============================================
echo       KITLUGIA - DEPLOY
echo  ===============================================
echo.

:: === STEP 1: VERSION DETECTION ===
echo  [1/6] Detecting latest version from GitHub...
echo.

set "VFILE=%TEMP%\kl_deploy_ver.txt"
set "IFILE=%TEMP%\kl_deploy_info.txt"
del "%VFILE%" 2>nul
del "%IFILE%" 2>nul

powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0kl_deploy_get_version.ps1" >nul 2>&1

:: Parse result
set "NEXT="
set "CURRENT="
set "VSOURCE="
set "STATUS=error"

if exist "%VFILE%" for /f "usebackq delims=" %%V in ("%VFILE%") do set "NEXT=%%V"
if exist "%IFILE%" for /f "tokens=2 delims==" %%A in ('findstr "^status=" "%IFILE%"') do set "STATUS=%%A"
if exist "%IFILE%" for /f "tokens=2 delims==" %%A in ('findstr "^source=" "%IFILE%"') do set "VSOURCE=%%A"
if exist "%IFILE%" for /f "tokens=2 delims==" %%A in ('findstr "^current=" "%IFILE%"') do set "CURRENT=%%A"

:: Show detection result - use goto to avoid if/else block issues
echo.
if not "!STATUS!"=="ok" goto :version_failed

echo  ===============================================
echo   Version Detection: SUCCESS
echo  ===============================================
echo.
echo   Source:        !VSOURCE!
echo   Current:       v!CURRENT!
echo   Next:          v!NEXT!
echo.
echo  ===============================================
echo.
goto :version_input

:version_failed
echo  ===============================================
echo   Version Detection: FAILED
echo  ===============================================
echo.
echo   All 4 automatic sources failed.
echo   Check: internet, gh auth, repo visibility.
echo.
echo   Create release at:
echo   https://github.com/luigiarrud4/KitLugia-AVTest/releases
echo.

:version_input
:: Version input
if not defined NEXT goto :manual_version
echo   Press ENTER to publish v!NEXT!
echo   Or type a different version, ex: 2.1.0
echo.
set /p "VER=  Version: "
if "!VER!"=="" set "VER=!NEXT!"
goto :check_ver

:manual_version
echo   Enter the version to publish, format X.Y.Z:
echo.
set /p "VER=  Version: "

:check_ver
if not defined VER (
    echo.
    echo  ERROR: Version is required.
    pause
    exit /b 1
)

:: Validate version format X.Y.Z
echo !VER! | findstr /r "^[0-9][0-9]*\.[0-9][0-9]*\.[0-9][0-9]*" >nul 2>&1
if !errorlevel! neq 0 (
    echo.
    echo  ERROR: Invalid format "!VER!" - use X.Y.Z
    set "VER="
    set /p "VER=  Version: "
    goto :check_ver
)

echo.
echo  -----------------------------------------------
echo   Publishing v!VER!...
echo  -----------------------------------------------
echo.

:: === STEP 2: AUTH ===
echo  [2/6] Authenticating with GitHub...
gh auth status >nul 2>&1
if !errorlevel! neq 0 (
    echo  Not authenticated. Attempting login...
    gh auth login -h github.com -w
    if !errorlevel! neq 0 (
        echo.
        echo  ERROR: GitHub auth failed.
        pause
        exit /b 1
    )
)
echo  OK
echo.

:: === STEP 3: BUILD ===
echo  [3/6] Building + ZIP + SHA256...
powershell -ExecutionPolicy Bypass -File "Deploy.ps1" -Version "!VER!"
if !errorlevel! neq 0 (
    echo.
    echo  ERROR: Deploy.ps1 failed.
    pause
    exit /b 1
)
echo.

:: === STEP 4: RELEASE ===
echo  [4/6] Creating GitHub release v!VER!...

gh release view "v!VER!" --repo "luigiarrud4/KitLugia-AVTest" >nul 2>&1
if !errorlevel! equ 0 (
    echo  Release v!VER! exists. Uploading assets...
    gh release upload "v!VER!" --repo "luigiarrud4/KitLugia-AVTest" ./Publish/KITLUGIA2.zip ./Publish/KITLUGIA2.zip.sha256 --clobber
    if !errorlevel! neq 0 echo  WARNING: Asset upload failed.
) else (
    gh release create "v!VER!" --repo "luigiarrud4/KitLugia-AVTest" --title "KitLugia v!VER!" --notes "Release automatica v!VER!" ./Publish/KITLUGIA2.zip ./Publish/KITLUGIA2.zip.sha256
    if !errorlevel! neq 0 echo  WARNING: Release creation failed.
)
echo.

:: === STEP 5: COMMIT ===
echo  [5/6] Git commit...
git add -A
set /p "MSG=  Message, Enter=default: "
if "!MSG!"=="" set "MSG=Deploy v!VER!"
git commit -m "!MSG!"
echo.

:: === STEP 6: PUSH ===
echo  [6/6] Git push + tag...
git push --set-upstream origin main 2>&1

git tag -f "v!VER!"
git push origin "v!VER!" --force 2>&1
if !errorlevel! neq 0 (
    echo  Tag push failed. Retrying...
    git push origin --delete "v!VER!" 2>&1
    git push origin "v!VER!" 2>&1
)

echo.
echo  ===============================================
echo   KITLUGIA v!VER! PUBLISHED!
echo  ===============================================
echo.
pause
endlocal
