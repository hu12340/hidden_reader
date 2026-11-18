@echo off
rem HiddenReader launcher (double-click to start).
rem No args: start the Debug build, building it first only if missing. "rebuild" arg: always build first.
rem Library defaults to %APPDATA%\HiddenReader; HR_DATA_DIR / HR_LOG_DIR env vars still override (for test rounds).
rem Keep this file ASCII + CRLF: UTF-8 Chinese text breaks cmd parsing (see OPERATING-NOTES).
setlocal
set "ROOT=%~dp0"
set "DOTNET=%ProgramFiles%\dotnet\dotnet.exe"
set "EXE=%ROOT%src\HiddenReader.App\bin\Debug\net10.0-windows\HiddenReader.exe"

if /i "%~1"=="rebuild" set "NEEDBUILD=1"
if not exist "%EXE%" set "NEEDBUILD=1"

if defined NEEDBUILD (
    if not exist "%DOTNET%" (
        echo [launcher] dotnet.exe not found: "%DOTNET%"
        pause
        exit /b 1
    )
    echo [launcher] Building HiddenReader.App ...
    "%DOTNET%" build "%ROOT%src\HiddenReader.App\HiddenReader.App.csproj" --nologo -v m
    if errorlevel 1 (
        echo [launcher] Build FAILED.
        pause
        exit /b 1
    )
)

if not exist "%EXE%" (
    echo [launcher] App exe not found: "%EXE%"
    pause
    exit /b 1
)

start "" "%EXE%"
endlocal
