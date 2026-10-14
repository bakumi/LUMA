@echo off
setlocal
cd /d "%~dp0"
set "DOTNET_CLI_HOME=%~dp0.cache\dotnet"
set "NUGET_PACKAGES=%~dp0.cache\nuget"
set "DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1"

if /i "%~1"=="build" (
  dotnet publish LUMA.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o "%~dp0artifacts\win-x64"
  exit /b %errorlevel%
)

dotnet run --project LUMA.csproj
exit /b %errorlevel%
