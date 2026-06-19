REM Example file - must be in same folder as the Windows ISO but have a .cmd file extension
REM When the WINDOWS VISTA,7,8,10,11 ISO file is run by agFM, under WinPE, this file will automatically run if it has the identical file name but a .cmd extension
REM - e.g. \_ISO\WINDOWS\WIN11\Win11.iso   and this file with same name e.g.  \_ISO\WINDOWS\WIN11\Win11.cmd
REM %FNAME% will already be the full path of the ISO directory - e.g. \_ISO\WINDOWS\WIN11\Win11
REM %isodrive% will already be the same drive letter that the ISO file is found on

echo on

REM save current dir  (probably X:\WINDOWS\SYSTEM32) and change to the directory containing this .cmd file
pushd %~dp0
dir /w /ad
pause

REM set USBDRIVE variable (USBDRIVE is used in legacy, isodrive is used in agFM)
if not DEFINED USBDRIVE set USBDRIVE=%isodrive%

echo %0 is running
echo %FNAME%.cmd running
REM change to the same drive letter that the ISO is in
cd %USBDRIVE%\
%USBDRIVE%
echo d0=%~d0 dp0=%~dp0 CD=%CD%
echo Variables are isodrive=%isodrive% FNAME=%FNAME% USBDRIVE=%USBDRIVE%
dir /w /ad
pause

echo now install oem driver using inf files + other files
REM install a mass storage driver - example
pnputil /add-driver %isodrive%\_ISO\WINDOWS\installs\DRIVERS\Drivers_5000\ia*.inf /subdirs /install
pause

REM Restore original dir (should be X:\WINDOWS\SYSTEM32)
popd
echo Finished
pause
echo off
