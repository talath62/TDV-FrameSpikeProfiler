@echo off
setlocal
if "%~1"=="" (
  echo Usage: BUILD.bat "C:\path\to\Valheim"
  exit /b 2
)
dotnet build "%~dp0TDVFrameSpikeProfiler.csproj" -c Release -p:ValheimDir="%~1"
exit /b %errorlevel%
