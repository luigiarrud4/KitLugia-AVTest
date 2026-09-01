@echo off
chcp 65001 >nul 2>&1
setlocal EnableDelayedExpansion
cd /d "%~dp0"

echo.
echo  =============================================
echo   KITLUGIA - Deploy Version Detection Tests
echo  =============================================
echo.

set "PASSED=0"
set "FAILED=0"
set "TESTDIR=%TEMP%\kl_deploy_test_%RANDOM%_%RANDOM%"
mkdir "%TESTDIR%" 2>nul

:: ── TEST 1: PowerShell script runs without error ──
echo  [TEST 1] PowerShell script execution...

set "VFILE=%TESTDIR%\kl_deploy_ver.txt"
set "IFILE=%TESTDIR%\kl_deploy_info.txt"

powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0kl_deploy_get_version.ps1" -OutputDir "%TESTDIR%" >"%TESTDIR%\stdout.txt" 2>"%TESTDIR%\stderr.txt"
set "PS_EXIT=%errorlevel%"

if !PS_EXIT! equ 0 (
    echo    PASS: Script exited with code 0
    set /a PASSED+=1
) else (
    echo    INFO: Script exited with code !PS_EXIT! - checking fallback...
    if exist "!IFILE!" (
        echo    PASS: Error info file was created - graceful fallback
        set /a PASSED+=1
    ) else (
        echo    FAIL: No output files created
        set /a FAILED+=1
    )
)

:: ── TEST 2: Output files exist ──
echo.
echo  [TEST 2] Output file creation...

set "T2_OK=1"
if exist "!VFILE!" (
    echo    PASS: kl_deploy_ver.txt exists
    set /a PASSED+=1
) else (
    echo    FAIL: kl_deploy_ver.txt not found
    set /a FAILED+=1
    set "T2_OK=0"
)

if exist "!IFILE!" (
    echo    PASS: kl_deploy_info.txt exists
    set /a PASSED+=1
) else (
    echo    FAIL: kl_deploy_info.txt not found
    set /a FAILED+=1
    set "T2_OK=0"
)

:: ── TEST 3: Version format is valid X.Y.Z ──
echo.
echo  [TEST 3] Version format validation...

if not exist "!VFILE!" (
    echo    SKIP: No version file to validate
    goto :test4
)

set /p VER_TEST=<"!VFILE!"
echo    Read version: !VER_TEST!

echo !VER_TEST!| findstr /r "^[0-9][0-9]*\.[0-9][0-9]*\.[0-9][0-9]*$" >nul 2>&1
if !errorlevel! equ 0 (
    echo    PASS: Version "!VER_TEST!" matches X.Y.Z format
    set /a PASSED+=1
) else (
    echo    FAIL: Version "!VER_TEST!" does not match X.Y.Z
    set /a FAILED+=1
)

:test4
:: ── TEST 4: Info file structure ──
echo.
echo  [TEST 4] Info file structure...

if not exist "!IFILE!" (
    echo    SKIP: No info file
    goto :test5
)

set "HAS_STATUS=0"
set "HAS_SOURCE=0"
set "HAS_CURRENT=0"
set "HAS_NEXT=0"

for /f "usebackq tokens=*" %%L in ("!IFILE!") do (
    echo %%L | findstr /C:"status" >nul 2>&1 && set "HAS_STATUS=1"
    echo %%L | findstr /C:"source" >nul 2>&1 && set "HAS_SOURCE=1"
    echo %%L | findstr /C:"current" >nul 2>&1 && set "HAS_CURRENT=1"
    echo %%L | findstr /C:"next" >nul 2>&1 && set "HAS_NEXT=1"
)

if "!HAS_STATUS!"=="1" (
    echo    PASS: Info has "status" field
    set /a PASSED+=1
) else (
    echo    FAIL: Info missing "status" field
    set /a FAILED+=1
)

if "!HAS_SOURCE!"=="1" (
    echo    PASS: Info has "source" field
    set /a PASSED+=1
) else (
    echo    FAIL: Info missing "source" field
    set /a FAILED+=1
)

if "!HAS_CURRENT!"=="1" (
    echo    PASS: Info has "current" field
    set /a PASSED+=1
) else (
    echo    FAIL: Info missing "current" field
    set /a FAILED+=1
)

if "!HAS_NEXT!"=="1" (
    echo    PASS: Info has "next" field
    set /a PASSED+=1
) else (
    echo    FAIL: Info missing "next" field
    set /a FAILED+=1
)

:test5
:: ── TEST 5: Next version = current + patch 1 ──
echo.
echo  [TEST 5] Next version = current + patch 1...

if not exist "!IFILE!" (
    echo    SKIP: No info file
    goto :test6
)

set "INFO_CURRENT="
set "INFO_NEXT="

for /f "tokens=2 delims=:, " %%A in ('findstr /C:"current" "!IFILE!"') do set "INFO_CURRENT=%%~A"
for /f "tokens=2 delims=:, " %%A in ('findstr /C:"next" "!IFILE!"') do set "INFO_NEXT=%%~A"

set "INFO_CURRENT=!INFO_CURRENT:"=!"
set "INFO_NEXT=!INFO_NEXT:"=!"

echo    Current: !INFO_CURRENT!
echo    Next:    !INFO_NEXT!

if not defined INFO_CURRENT goto :test5_skip
if not defined INFO_NEXT goto :test5_skip

for /f "tokens=3 delims=." %%P in ("!INFO_CURRENT!") do set "CUR_PATCH=%%P"
for /f "tokens=3 delims=." %%Q in ("!INFO_NEXT!") do set "NEXT_PATCH=%%Q"

set /a EXPECTED_PATCH=!CUR_PATCH!+1
if "!NEXT_PATCH!"=="!EXPECTED_PATCH!" (
    echo    PASS: Patch incremented correctly "!CUR_PATCH! -^> !NEXT_PATCH!"
    set /a PASSED+=1
) else (
    echo    FAIL: Next patch !NEXT_PATCH! should be !EXPECTED_PATCH!
    set /a FAILED+=1
)
goto :test6

:test5_skip
echo    SKIP: Could not parse current/next

:test6
:: ── TEST 6: Source field is valid ──
echo.
echo  [TEST 6] Source field is a valid fallback name...

if not exist "!IFILE!" (
    echo    SKIP: No info file
    goto :test7
)

set "INFO_SOURCE="
for /f "tokens=2 delims=:, " %%A in ('findstr /C:"source" "!IFILE!"') do set "INFO_SOURCE=%%~A"
set "INFO_SOURCE=!INFO_SOURCE:"=!"

set "SOURCE_OK=0"
if "!INFO_SOURCE!"=="gh-cli" set "SOURCE_OK=1"
if "!INFO_SOURCE!"=="invoke-restmethod" set "SOURCE_OK=1"
if "!INFO_SOURCE!"=="curl" set "SOURCE_OK=1"
if "!INFO_SOURCE!"=="git-tag-offline" set "SOURCE_OK=1"
if "!INFO_SOURCE!"=="none" set "SOURCE_OK=1"

if "!SOURCE_OK!"=="1" (
    echo    PASS: Source = !INFO_SOURCE! - valid fallback
    set /a PASSED+=1
) else (
    echo    FAIL: Source "!INFO_SOURCE!" is not recognized
    set /a FAILED+=1
)

:test7
:: ── TEST 7: deploy.bat parses correctly ──
echo.
echo  [TEST 7] deploy.bat version parsing simulation...

if not exist "!VFILE!" (
    echo    SKIP: No version file
    goto :test8
)

set "BATCH_VER="
set /p "BATCH_VER=<"!VFILE!""

if defined BATCH_VER (
    echo    PASS: deploy.bat can read NEXT = "!BATCH_VER!"
    set /a PASSED+=1
) else (
    echo    FAIL: deploy.bat reads empty NEXT
    set /a FAILED+=1
)

:test8
:: ── TEST 8: PowerShell version parsing (direct) ──
echo.
echo  [TEST 8] PowerShell version parsing - 4 cases...

powershell -NoProfile -ExecutionPolicy Bypass -Command "$ErrorActionPreference='Stop'; function Get-NV { param([string]$t); $v=$t -replace '^[vV]',''; if ($v -notmatch '^\d+\.\d+\.\d+$') { throw 'bad' }; $p=$v.Split('.'); $np=[int]$p[2]+1; return '{0}.{1}.{2}' -f $p[0],$p[1],$np }; $ok=0; $fail=0; @(@('v2.0.52','2.0.53'),@('v1.0.0','1.0.1'),@('v2.0.99','2.0.100'),@('V10.20.30','10.20.31')) | ForEach-Object { $r=Get-NV $_[0]; if($r-eq $_[1]){$ok++}else{Write-Host ('FAIL: ' + $_[0] + ' -> ' + $r); $fail++} }; if($fail -gt 0){exit 1}" >nul 2>&1

if !errorlevel! equ 0 (
    echo    PASS: All 4 version parsing cases correct
    set /a PASSED+=1
) else (
    echo    FAIL: Some version parsing cases failed
    set /a FAILED+=1
)

:: ── RESULTS ──
echo.
echo  =============================================
echo   RESULTS
echo  =============================================
echo   Passed:  !PASSED!
echo   Failed:  !FAILED!
echo.

rmdir /s /q "%TESTDIR%" 2>nul

if !FAILED! gtr 0 (
    echo   SOME TESTS FAILED!
    exit /b 1
) else (
    echo   ALL TESTS PASSED!
    exit /b 0
)

endlocal
