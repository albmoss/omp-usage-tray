@echo off
rem Stops the widget and removes it, its data directory and the omp extension.
setlocal
taskkill /im omp-usage-tray.exe /f >nul 2>&1
del /q "%USERPROFILE%\.omp\agent\extensions\usage-tray.ts" 2>nul
if exist "%LOCALAPPDATA%\omp-usage-tray" rmdir /s /q "%LOCALAPPDATA%\omp-usage-tray"
echo Removed. Restart open omp sessions to unload the extension.
