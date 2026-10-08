@echo off
setlocal
REM Builds dist\OrclCM.exe - a native Windows app for the .NET Framework 4.8 that ships
REM with Windows 10/11, so nothing needs installing to run it.
REM Needs the C# compiler from Visual Studio or the VS Build Tools (found via vswhere),
REM or set CSC to a Roslyn csc.exe. Runs the tests first and stops at the first failure.
REM Set NOPAUSE=1 to skip the final pause.
cd /d "%~dp0"

if defined CSC goto :have_csc
set "VSWHERE=%ProgramFiles(x86)%\Microsoft Visual Studio\Installer\vswhere.exe"
if not exist "%VSWHERE%" goto :no_csc
for /f "usebackq delims=" %%i in (`"%VSWHERE%" -latest -products * -find MSBuild\**\Bin\Roslyn\csc.exe`) do set "CSC=%%i"
if not defined CSC goto :no_csc
:have_csc

set "FW=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319"
set REFS=/nostdlib+ /r:"%FW%\mscorlib.dll" /r:"%FW%\System.dll" /r:"%FW%\System.Core.dll" /r:"%FW%\System.Drawing.dll" /r:"%FW%\System.Windows.Forms.dll"
set OPTS=/nologo /noconfig /langversion:latest /optimize+ /warnaserror+ /platform:anycpu /utf8output %REFS%
set "OUT=dist\OrclCM.exe"
if not exist build mkdir build
if not exist dist mkdir dist

echo [1/3] Building and running tests
"%CSC%" %OPTS% /target:exe /out:build\CoreTests.exe src\AppInfo.cs src\Lang.cs src\Core.cs src\Features.cs src\TrayIconRenderer.cs tests\CoreTests.cs
if errorlevel 1 goto :fail
build\CoreTests.exe
if errorlevel 1 goto :fail

echo [2/3] Removing previous build output
if exist "%OUT%" del /f /q "%OUT%"
if exist "%OUT%" (
    echo Could not delete the old %OUT% - is OrclCM still running?
    goto :fail
)

echo [3/3] Building %OUT%
"%CSC%" %OPTS% /target:winexe /out:"%OUT%" /win32icon:assets\icon.ico /win32manifest:src\app.manifest src\*.cs
if errorlevel 1 goto :fail
if not exist "%OUT%" goto :fail

echo.
echo Done: %OUT%
if not defined NOPAUSE pause
exit /b 0

:no_csc
echo C# compiler not found. Install Visual Studio or "Build Tools for Visual Studio"
echo (workload: .NET desktop build tools), or set CSC to the path of csc.exe.
:fail
echo.
echo Build FAILED - see the messages above. No new exe was produced.
if not defined NOPAUSE pause
exit /b 1
