@echo off
rem Double-click to sign publish\Kelvra.exe. Pass extra options through, e.g.:
rem   sign.cmd -Publisher "Your Name" -TrustOnThisPC
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0sign.ps1" %*
echo.
pause
