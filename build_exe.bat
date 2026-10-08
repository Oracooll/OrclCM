@echo off
setlocal
REM Builds a portable single-file OrclCM.exe into .\dist. The version (VERSION in
REM orclcm.py) appears in the window title bar and the exe's file properties.
REM Uses an isolated build environment (.build-venv) with the pinned versions from
REM requirements-build.txt, so your global Python packages are never touched.
REM Stops at the first failing step; "Done" is only printed if a new exe was produced.
REM Optional: set PYTHON to another interpreter command (default: python),
REM           set NOPAUSE=1 to skip the final pause.
cd /d "%~dp0"

if not defined PYTHON set "PYTHON=python"
if not defined BUILD_VENV set "BUILD_VENV=%~dp0.build-venv"
set "VPY=%BUILD_VENV%\Scripts\python.exe"

if exist "%VPY%" goto :have_venv
echo [1/5] Creating isolated build environment in %BUILD_VENV%
%PYTHON% -m venv "%BUILD_VENV%"
if errorlevel 1 goto :fail
if not exist "%VPY%" goto :fail
:have_venv

echo [2/5] Installing pinned build dependencies
"%VPY%" -m pip install --disable-pip-version-check -r requirements-build.txt
if errorlevel 1 goto :fail

echo [3/5] Reading version
set "VERSION="
for /f "delims=" %%v in ('""%VPY%" build_version.py"') do set "VERSION=%%v"
if not defined VERSION goto :fail
set "NAME=OrclCM"
set "OUT=dist\%NAME%.exe"
echo Version %VERSION%

echo [4/5] Removing previous build output
if exist "%OUT%" del /f /q "%OUT%"
if exist "%OUT%" (
    echo Could not delete the old %OUT% - is OrclCM still running?
    goto :fail
)

echo [5/5] Packaging
"%VPY%" -m PyInstaller --noconfirm --clean --onefile --windowed --name %NAME% --icon assets\icon.ico --version-file build\version_info.txt --hidden-import pystray._win32 orclcm.py
if errorlevel 1 goto :fail
if not exist "%OUT%" (
    echo PyInstaller reported success but %OUT% was not created.
    goto :fail
)

echo.
echo Done: %OUT%
if not defined NOPAUSE pause
exit /b 0

:fail
echo.
echo Build FAILED - see the messages above. No new exe was produced.
if not defined NOPAUSE pause
exit /b 1
