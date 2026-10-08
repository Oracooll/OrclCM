@echo off
cd /d "%~dp0"
python orclcm.py
if errorlevel 1 pause
