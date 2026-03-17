@echo off
REM publish-all.bat
REM Publishes CrossPlatformPatcher for all supported runtime identifiers:
REM   win-x64, linux-x64, osx-x64, osx-arm64
REM Run from the repository root.

set OUT=publish
set PROJECT=CrossPlatformPatcher.csproj
set COMMON=-c Release --self-contained true -p:PublishSingleFile=true

echo.
echo =^> Publishing CrossPlatformPatcher for win-x64 ...
dotnet publish "%PROJECT%" %COMMON% -r win-x64 -o "%OUT%\CrossPlatformPatcher\win-x64"

echo.
echo =^> Publishing CrossPlatformPatcher for linux-x64 ...
dotnet publish "%PROJECT%" %COMMON% -r linux-x64 -o "%OUT%\CrossPlatformPatcher\linux-x64"

echo.
echo =^> Publishing CrossPlatformPatcher for osx-x64 ...
dotnet publish "%PROJECT%" %COMMON% -r osx-x64 -o "%OUT%\CrossPlatformPatcher\osx-x64"

echo.
echo =^> Publishing CrossPlatformPatcher for osx-arm64 ...
dotnet publish "%PROJECT%" %COMMON% -r osx-arm64 -o "%OUT%\CrossPlatformPatcher\osx-arm64"

echo.
echo All builds complete:
echo   %OUT%\CrossPlatformPatcher\win-x64
echo   %OUT%\CrossPlatformPatcher\linux-x64
echo   %OUT%\CrossPlatformPatcher\osx-x64
echo   %OUT%\CrossPlatformPatcher\osx-arm64
