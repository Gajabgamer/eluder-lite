@echo off
title Eluder — Build
echo.
echo  Building Eluder.exe ...
echo.

dotnet publish MyOverlayPOC.csproj -c Release -r win-x64 ^
  --self-contained true ^
  -p:PublishSingleFile=true ^
  -p:IncludeNativeLibrariesForSelfExtract=true ^
  -p:EnableCompressionInSingleFile=true ^
  -p:PublishReadyToRun=true ^
  -p:AssemblyName=Eluder ^
  -o ./publish

echo.
if %errorlevel%==0 (
    echo  [OK] Build successful!
    echo  Output: %~dp0publish\Eluder.exe
    explorer "%~dp0publish"
) else (
    echo  [ERR] Build failed. Check errors above.
)
echo.
pause
