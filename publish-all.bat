@echo off
REM publish-all.bat
REM Publishes CrossPlatformPatcher for all supported runtime identifiers:
REM   win-x64, linux-x64, osx-x64, osx-arm64
REM Run from the repository root.

set OUT=publish
set PROJECT=CrossPlatformPatcher.csproj
set SETUP_PROJECT=InstallerWizard\InstallerWizard.csproj
set COMMON=-c Release --self-contained true -p:PublishSingleFile=true

echo.
echo =^> Publishing CrossPlatformPatcher for Windows x64 ...
dotnet publish "%PROJECT%" %COMMON% -r win-x64 -p:AssemblyName=CrossPlatformPatcher-W-x64 -o "%OUT%\CrossPlatformPatcher\win-x64"
dotnet publish "%SETUP_PROJECT%" %COMMON% -r win-x64 -p:AssemblyName=SetupWizard -o "%OUT%\SetupWizard\win-x64"

echo.
echo =^> Publishing CrossPlatformPatcher for Linux x64 ...
dotnet publish "%PROJECT%" %COMMON% -r linux-x64 -p:AssemblyName=CrossPlatformPatcher-L-x64 -o "%OUT%\CrossPlatformPatcher\linux-x64"
dotnet publish "%SETUP_PROJECT%" %COMMON% -r linux-x64 -p:AssemblyName=SetupWizard -o "%OUT%\SetupWizard\linux-x64"

echo.
echo =^> Publishing CrossPlatformPatcher for macOS x64 ...
dotnet publish "%PROJECT%" %COMMON% -r osx-x64 -p:AssemblyName=CrossPlatformPatcher-M-x64 -o "%OUT%\CrossPlatformPatcher\osx-x64"
dotnet publish "%SETUP_PROJECT%" %COMMON% -r osx-x64 -p:AssemblyName=SetupWizard -o "%OUT%\SetupWizard\osx-x64"

echo.
echo =^> Publishing CrossPlatformPatcher for macOS ARM64 ...
dotnet publish "%PROJECT%" %COMMON% -r osx-arm64 -p:AssemblyName=CrossPlatformPatcher-M-Arm -o "%OUT%\CrossPlatformPatcher\osx-arm64"
dotnet publish "%SETUP_PROJECT%" %COMMON% -r osx-arm64 -p:AssemblyName=SetupWizard -o "%OUT%\SetupWizard\osx-arm64"

echo.
echo All builds complete:
echo   %OUT%\CrossPlatformPatcher\win-x64\CrossPlatformPatcher-W-x64.exe
echo   %OUT%\CrossPlatformPatcher\linux-x64\CrossPlatformPatcher-L-x64
echo   %OUT%\CrossPlatformPatcher\osx-x64\CrossPlatformPatcher-M-x64
echo   %OUT%\CrossPlatformPatcher\osx-arm64\CrossPlatformPatcher-M-Arm
echo   %OUT%\SetupWizard\win-x64\SetupWizard.exe
echo   %OUT%\SetupWizard\linux-x64\SetupWizard
echo   %OUT%\SetupWizard\osx-x64\SetupWizard
echo   %OUT%\SetupWizard\osx-arm64\SetupWizard
