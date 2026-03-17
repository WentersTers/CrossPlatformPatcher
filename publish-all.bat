@echo off
REM publish-all.bat
REM Publishes both product variants:
REM   1) PAIcomPatcher.HotSwap.Win (win-x64 only)
REM   2) PAIcomPatcher.Compat (win-x64, linux-x64, osx-x64, osx-arm64)
REM Run from the repository root.

set OUT=publish
set COMMON=-c Release --self-contained true -p:PublishSingleFile=true

echo.
echo =^> Publishing PAIcomPatcher.HotSwap.Win for win-x64 ...
dotnet publish "PAIcomPatcher.HotSwap.Win.csproj" %COMMON% -r win-x64 -o "%OUT%\PAIcomPatcher.HotSwap.Win\win-x64"

echo.
echo =^> Publishing PAIcomPatcher.Compat for win-x64 ...
dotnet publish "PAIcomPatcher.Compat.csproj" %COMMON% -r win-x64 -o "%OUT%\PAIcomPatcher.Compat\win-x64"

echo.
echo =^> Publishing PAIcomPatcher.Compat for linux-x64 ...
dotnet publish "PAIcomPatcher.Compat.csproj" %COMMON% -r linux-x64 -o "%OUT%\PAIcomPatcher.Compat\linux-x64"

echo.
echo =^> Publishing PAIcomPatcher.Compat for osx-x64 ...
dotnet publish "PAIcomPatcher.Compat.csproj" %COMMON% -r osx-x64 -o "%OUT%\PAIcomPatcher.Compat\osx-x64"

echo.
echo =^> Publishing PAIcomPatcher.Compat for osx-arm64 ...
dotnet publish "PAIcomPatcher.Compat.csproj" %COMMON% -r osx-arm64 -o "%OUT%\PAIcomPatcher.Compat\osx-arm64"

echo.
echo All builds complete:
echo   %OUT%\PAIcomPatcher.HotSwap.Win\win-x64
echo   %OUT%\PAIcomPatcher.Compat\win-x64
echo   %OUT%\PAIcomPatcher.Compat\linux-x64
echo   %OUT%\PAIcomPatcher.Compat\osx-x64
echo   %OUT%\PAIcomPatcher.Compat\osx-arm64
