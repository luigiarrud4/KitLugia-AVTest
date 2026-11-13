@echo off
chcp 65001 >nul 2>&1
setlocal EnableDelayedExpansion
cd /d "%~dp0"

if exist "C:\Program Files\Git\cmd\git.exe" set "PATH=C:\Program Files\Git\cmd;C:\Program Files\Git\bin;%PATH%"
if exist "C:\Program Files\GitHub CLI\gh.exe" set "PATH=C:\Program Files\GitHub CLI;%PATH%"

set "REPO_OWNER=luigiarrud4"
set "REPO=%REPO_OWNER%/KitLugia-AVTest"
set "TARGET_BRANCH=main"
set "ZIP=%~dp0Publish\KITLUGIA2.zip"
set "HASH=%~dp0Publish\KITLUGIA2.zip.sha256"
set "VFILE=%TEMP%\kl_deploy_ver.txt"
set "IFILE=%TEMP%\kl_deploy_info.txt"

echo.
echo  ===============================================
echo       KITLUGIA - DEPLOY
echo  ===============================================
echo.

:: === STEP 1: VERSION DETECTION ===
echo  [1/6] Detecting latest version from GitHub...
echo.

del "%VFILE%" 2>nul
del "%IFILE%" 2>nul

powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0kl_deploy_get_version.ps1" >nul 2>&1

set "NEXT="
set "CURRENT="
set "VSOURCE="
set "STATUS=error"

if exist "%VFILE%" for /f "usebackq delims=" %%V in ("%VFILE%") do set "NEXT=%%V"
if exist "%IFILE%" for /f "tokens=2 delims==" %%A in ('findstr "^status=" "%IFILE%"') do set "STATUS=%%A"
if exist "%IFILE%" for /f "tokens=2 delims==" %%A in ('findstr "^source=" "%IFILE%"') do set "VSOURCE=%%A"
if exist "%IFILE%" for /f "tokens=2 delims==" %%A in ('findstr "^current=" "%IFILE%"') do set "CURRENT=%%A"

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
echo   All automatic sources failed.
echo   Check: internet, gh auth, repo visibility.
echo   Releases: https://github.com/%REPO%/releases
echo.

:version_input
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

:: Anchored at both ends: "2.1.0junk" must NOT pass. No space before the pipe.
echo(!VER!| findstr /r "^[0-9][0-9]*\.[0-9][0-9]*\.[0-9][0-9]*$" >nul 2>&1
if !errorlevel! neq 0 (
    echo.
    echo  ERROR: Invalid format "!VER!" - use X.Y.Z
    set "VER="
    set /p "VER=  Version: "
    goto :check_ver
)

:: Warn about a version whose tag/release already exists.
git ls-remote --exit-code --tags origin "refs/tags/v!VER!" >nul 2>&1
if !errorlevel! equ 0 (
    gh release view "v!VER!" --repo "%REPO%" >nul 2>&1
    if !errorlevel! equ 0 (
        echo.
        echo  NOTE: release v!VER! already exists - assets will be replaced.
    ) else (
        echo.
        echo  NOTE: tag v!VER! exists without a release - retargeting it to HEAD.
    )
)

echo.
echo  -----------------------------------------------
echo   Publishing v!VER!...
echo  -----------------------------------------------
echo.

:: === STEP 2: AUTH (activate + VERIFY the account that owns the repo) ===
echo  [2/6] Activating GitHub account %REPO_OWNER%...

where gh >nul 2>&1
if !errorlevel! neq 0 (
    echo  ERROR: GitHub CLI ^(gh^) not found in PATH.
    pause
    exit /b 1
)
where git >nul 2>&1
if !errorlevel! neq 0 (
    echo  ERROR: git not found in PATH.
    pause
    exit /b 1
)

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

:: "Authenticated" is not enough: it must be the account that owns the repo.
:: Otherwise gh release succeeds on the wrong account while git push gets 403.
set "GH_USER="
for /f "tokens=1" %%A in ('gh api user --jq .login 2^>nul') do set "GH_USER=%%A"
if /i not "!GH_USER!"=="%REPO_OWNER%" (
    echo  Active account "!GH_USER!" is not %REPO_OWNER% - switching...
    gh auth switch -u %REPO_OWNER% >nul 2>&1
    if !errorlevel! neq 0 gh auth login -h github.com -w
    set "GH_USER="
    for /f "tokens=1" %%A in ('gh api user --jq .login 2^>nul') do set "GH_USER=%%A"
)
if /i not "!GH_USER!"=="%REPO_OWNER%" (
    echo.
    echo  ERROR: active gh account is "!GH_USER!", expected %REPO_OWNER%.
    echo         Run: gh auth login -h github.com -w
    echo         or:  gh auth switch -u %REPO_OWNER%
    pause
    exit /b 1
)
echo  GitHub account: %REPO_OWNER% OK

:: git MUST use the same token. A stale Windows Credential Manager entry for
:: another account makes "git push" return 403 while "gh release" still works.
gh auth setup-git >nul 2>&1
if !errorlevel! neq 0 (
    echo.
    echo  ERROR: gh auth setup-git failed - git would use a different credential.
    pause
    exit /b 1
)
set "GIT_USER="
call :git_cred_user
if /i "!GIT_USER!"=="%REPO_OWNER%" goto :git_auth_ok

:: Missing or foreign credential: force the interactive git login instead of
:: stopping here and asking the user to clean Credential Manager by hand.
echo.
echo  Git credential missing or for another account ^(found: "!GIT_USER!"^).
echo  Opening the git login prompt for github.com...
call :git_login_prompt
call :git_cred_user
if /i "!GIT_USER!"=="%REPO_OWNER%" goto :git_auth_ok

echo.
echo  ERROR: git is still using account "!GIT_USER!", expected %REPO_OWNER%.
echo         Sign in as %REPO_OWNER% in the window/browser that opened, then
echo         rerun deploy.bat. Manual fix: delete "git:https://github.com"
echo         in Windows Credential Manager and run: gh auth setup-git
pause
exit /b 1

:git_auth_ok
echo  Git credential: %REPO_OWNER% OK
echo  OK
echo.

:: === STEP 3: BUILD ===
echo  [3/6] Building + ZIP + SHA256...
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0Deploy.ps1" -Version "!VER!"
if !errorlevel! neq 0 (
    echo.
    echo  ERROR: Deploy.ps1 failed.
    pause
    exit /b 1
)
if not exist "%ZIP%" (
    echo  ERROR: ZIP not generated: "%ZIP%"
    pause
    exit /b 1
)
if not exist "%HASH%" (
    echo  ERROR: SHA256 not generated: "%HASH%"
    pause
    exit /b 1
)
echo  OK
echo.

:: === STEP 4: COMMIT + PUSH (BEFORE the release) ===
:: HEAD:main pushes the CURRENT branch, not the local "main" ref. Using the
:: plain "main" refspec silently pushed a stale branch when working on "aaa".
echo  [4/6] Git commit + push to %TARGET_BRANCH%...

git add -A
if !errorlevel! neq 0 goto :stage_failed

set /p "MSG=  Message, Enter=default: "
if "!MSG!"=="" set "MSG=Deploy v!VER!"

git diff --cached --quiet >nul 2>&1
if !errorlevel! equ 0 (
    echo  No staged changes - skipping commit.
) else (
    git commit -m "!MSG!"
    if !errorlevel! neq 0 goto :commit_failed
)

git push origin HEAD:%TARGET_BRANCH%
if !errorlevel! neq 0 goto :push_failed
echo  OK
echo.
goto :git_done

:stage_failed
echo.
echo  ERROR: git add -A failed. No release was published.
pause
exit /b 1

:commit_failed
echo.
echo  ERROR: git commit was REJECTED. No release was published.
echo  Auth is NOT the cause here - step 2 already verified %REPO_OWNER%.
echo  This is usually a PRE-COMMIT HOOK (.githooks/pre-commit -> ui_style_audit).
echo  Read the report printed above. Fix the XAML, or if the change is
echo  intentional, update the allowlist:
echo      py -3 tests/ui_style_audit.py --baseline
echo  Files remain staged; the build is already done. Safe to rerun deploy.bat.
pause
exit /b 1

:push_failed
echo.
echo  ERROR: git push failed ^(auth, network or diverged branch^).
echo  The commit exists locally; no release was published.
echo  Verify with: git push --dry-run origin HEAD:%TARGET_BRANCH%
pause
exit /b 1

:git_done

:: === STEP 5: RELEASE ===
echo  [5/6] Creating GitHub release v!VER!...

gh release view "v!VER!" --repo "%REPO%" >nul 2>&1
if !errorlevel! equ 0 (
    echo  Release v!VER! exists. Replacing assets...
    gh release upload "v!VER!" --repo "%REPO%" "%ZIP%" "%HASH%" --clobber
    if !errorlevel! neq 0 goto :release_failed
    goto :release_done
)

:: No release. If a tag already exists orphaned (tag but no release), point it
:: at the commit we just pushed so the release source matches the uploaded ZIP.
git ls-remote --exit-code --tags origin "refs/tags/v!VER!" >nul 2>&1
if !errorlevel! equ 0 (
    echo  Orphan tag found - retargeting v!VER! to HEAD...
    git tag -f "v!VER!" HEAD >nul 2>&1
    git push origin "refs/tags/v!VER!" --force
    if !errorlevel! neq 0 goto :release_failed
)

:: --generate-notes = o botao "Generate release notes" do GitHub (Release Notes
:: API): a IA do GitHub gera a descricao + a comparacao de versoes (PRs,
:: contribuidores e link do changelog desde a release anterior). O --title
:: mantem "KitLugia vX.Y.Z"; o corpo vem 100% do GitHub.
echo  Release notes: generating with GitHub AI...
gh release create "v!VER!" --repo "%REPO%" --target %TARGET_BRANCH% --title "KitLugia v!VER!" --generate-notes "%ZIP%" "%HASH%"
if !errorlevel! equ 0 goto :release_done

:: Fallback: o codigo ja foi empurrado - a API de notas falhou (rate limit,
:: indisponibilidade). Nao deixar a release inteira morrer: tenta outra vez com
:: uma nota minima. Se o --generate-notes ja tiver criado a release, o create
:: falha e o :release_failed abaixo explica o caminho manual.
echo  AI notes failed - retrying release with basic notes...
gh release create "v!VER!" --repo "%REPO%" --target %TARGET_BRANCH% --title "KitLugia v!VER!" --notes "KitLugia v!VER! - release automatica (notas IA indisponiveis)" "%ZIP%" "%HASH%"
if !errorlevel! neq 0 goto :release_failed
goto :release_done

:release_failed
echo.
echo  ERROR: release creation/upload failed. The code was already pushed.
echo  Retry: gh release upload v!VER! --repo %REPO% "%ZIP%" "%HASH%" --clobber
pause
exit /b 1

:release_done
echo  OK
echo.

:: === STEP 6: VERIFY ===
echo  [6/6] Verifying published release...

set "LOCALHEAD="
for /f "delims=" %%H in ('git rev-parse HEAD') do set "LOCALHEAD=%%H"
set "TAGHEAD="
for /f "tokens=1" %%H in ('git ls-remote origin "refs/tags/v!VER!" 2^>nul') do set "TAGHEAD=%%H"

gh release view "v!VER!" --repo "%REPO%" --json assets --jq ".assets[].name" 2>nul
if !errorlevel! neq 0 (
    echo  WARNING: could not list release assets.
)

echo.
echo  Release notes (geradas pela IA do GitHub):
echo  -----------------------------------------------
gh release view "v!VER!" --repo "%REPO%" --json body --jq ".body" 2>nul
if !errorlevel! neq 0 (
    echo  WARNING: could not read release notes.
)

if /i "!TAGHEAD!"=="!LOCALHEAD!" (
    echo  Tag v!VER! points to HEAD ^(!LOCALHEAD:~0,7!^) - OK
) else (
    echo  WARNING: tag v!VER! does not point to HEAD ^(!LOCALHEAD:~0,7!^).
    echo           Release source may be older than the uploaded ZIP.
)

echo.
echo  ===============================================
echo   KITLUGIA v!VER! PUBLISHED!
echo   https://github.com/%REPO%/releases/tag/v!VER!
echo  ===============================================
echo.
pause
goto :kl_deploy_end

:: ---------------------------------------------------------------
:: Helper routines (kept inside setlocal so %REPO% / %REPO_OWNER% resolve)
:: ---------------------------------------------------------------

:: Prints the username git would authenticate with for github.com.
:: Detection only - it must never block on a prompt.
:git_cred_user
set "GIT_USER="
set "GIT_TERMINAL_PROMPT=0"
set "GCM_INTERACTIVE=Never"
set "CREDREQ=%TEMP%\kl_deploy_cred_req.txt"
> "%CREDREQ%" echo protocol=https
>> "%CREDREQ%" echo host=github.com
>> "%CREDREQ%" echo.
for /f "tokens=2 delims== " %%A in ('type "%CREDREQ%" ^| git credential fill 2^>nul ^| findstr /b "username="') do set "GIT_USER=%%A"
del "%CREDREQ%" 2>nul
set "GIT_TERMINAL_PROMPT="
set "GCM_INTERACTIVE="
exit /b 0

:: Forgets the stored github.com credential and lets git ask for a login.
:: gh feeds git its own token silently through a host helper, so that helper is
:: removed for github.com - otherwise the login prompt would never appear.
:git_login_prompt
set "CREDREQ=%TEMP%\kl_deploy_cred_req.txt"
> "%CREDREQ%" echo protocol=https
>> "%CREDREQ%" echo host=github.com
>> "%CREDREQ%" echo username=%REPO_OWNER%
>> "%CREDREQ%" echo.
type "%CREDREQ%" | git credential reject >nul 2>&1
del "%CREDREQ%" 2>nul
cmdkey /delete:git:https://github.com >nul 2>&1
git config --local --unset-all credential.https://github.com.helper >nul 2>&1
git config --global --unset-all credential.https://github.com.helper >nul 2>&1
echo.
echo   Sign in as %REPO_OWNER% in the GitHub window/browser that opens.
echo.
set "GIT_TERMINAL_PROMPT=1"
git ls-remote "https://github.com/%REPO%.git" HEAD >nul 2>&1
set "GIT_TERMINAL_PROMPT="
echo.
exit /b 0

:kl_deploy_end
endlocal
exit /b 0
