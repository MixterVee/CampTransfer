@echo off
setlocal
cd /d "%~dp0"

where dotnet >nul 2>nul
if errorlevel 1 (
    echo .NET 8 SDK was not found.
    echo Install the .NET 8 SDK, then run this file again.
    echo.
    pause
    exit /b 1
)

echo Restoring Turtle Transfer artwork...
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0build\GenerateTurtleIcon.ps1" -OutputPath "%~dp0assets\TurtleTransfer.ico"
if errorlevel 1 (
    echo.
    echo Artwork generation failed.
    pause
    exit /b 1
)

echo Building Turtle Transfer for 64-bit Windows...
dotnet publish CampTransfer.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o publish
if errorlevel 1 (
    echo.
    echo Build failed.
    pause
    exit /b 1
)

echo.
echo Build complete:
echo %~dp0publish\TurtleTransfer.exe
pause
