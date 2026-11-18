@echo off
REM Build do Kit Task Manager nativo (C++ Win32 + D3D11 + D2D1/DWrite)
REM Uso: build.bat  (requer MSVC — Visual Studio 2022/18)
setlocal
set "VSDIR=C:\Program Files\Microsoft Visual Studio\18\Community"
if not exist "%VSDIR%\VC\Auxiliary\Build\vcvars64.bat" (
    for /f "usebackq delims=" %%i in (`"%ProgramFiles(x86)%\Microsoft Visual Studio\Installer\vswhere.exe" -latest -property installationPath`) do set "VSDIR=%%i"
)
call "%VSDIR%\VC\Auxiliary\Build\vcvars64.bat" >nul
if errorlevel 1 ( echo ERRO: vcvars64 nao encontrado & exit /b 1 )
cl /nologo /EHsc /O2 /std:c++17 /W3 /DUNICODE /D_UNICODE ^
   main.cpp render.cpp metrics.cpp ^
   /Fe:KitTaskManager.exe /Fo:obj\ ^
   /link user32.lib gdi32.lib comctl32.lib d3d11.lib dxgi.lib d2d1.lib dwrite.lib pdh.lib psapi.lib advapi32.lib wtsapi32.lib iphlpapi.lib
if errorlevel 1 ( echo FALHOU & exit /b 1 )
echo OK: KitTaskManager.exe
endlocal
