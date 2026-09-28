@echo off
rem Builds omp-usage-tray.exe with the C# compiler that ships with Windows (.NET Framework 4.x)
rem and installs it together with the omp extension that feeds it. Safe to re-run after updates.
setlocal
set "SRC=%~dp0src"
set "DEST=%LOCALAPPDATA%\omp-usage-tray"
set "EXT=%USERPROFILE%\.omp\agent\extensions"
set "CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if not exist "%CSC%" set "CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe"
if not exist "%CSC%" (
    echo C# compiler from .NET Framework 4.x not found under %WINDIR%\Microsoft.NET.
    exit /b 1
)

if not exist "%DEST%" mkdir "%DEST%"
if not exist "%EXT%" mkdir "%EXT%"
taskkill /im omp-usage-tray.exe /f >nul 2>&1

"%CSC%" /nologo /target:winexe /optimize+ /win32manifest:"%SRC%\app.manifest" /out:"%DEST%\omp-usage-tray.exe" ^
    /r:System.Windows.Forms.dll /r:System.Drawing.dll "%SRC%\UsageTray.cs" || exit /b 1
copy /y "%SRC%\app.config" "%DEST%\omp-usage-tray.exe.config" >nul || exit /b 1
copy /y "%~dp0extension\usage-tray.ts" "%EXT%\usage-tray.ts" >nul || exit /b 1

rem Picks up omp sessions that are already running; exits on its own after 5 s when there are none.
start "" "%DEST%\omp-usage-tray.exe"
echo Installed to %DEST%
echo Extension: %EXT%\usage-tray.ts
echo Restart open omp sessions so they load the extension.
