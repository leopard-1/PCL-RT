@echo off
rem Build PCL-Rebuilder.exe from source. Real work is in build.ps1 (robust compiler lookup).
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0build.ps1" %*
echo.
pause