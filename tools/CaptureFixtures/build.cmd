@echo off
rem Publishes CaptureFixtures in Release to %LOCALAPPDATA%\blp-net, outside
rem Dropbox. Needs the .NET 10 SDK and the repo checkout, whose nuget.config
rem and packages\ folder supply the Bloomberg SDK.
setlocal
set "OUT=%LOCALAPPDATA%\blp-net\CaptureFixtures"
dotnet publish "%~dp0CaptureFixtures.csproj" -c Release -o "%OUT%"
if errorlevel 1 exit /b %errorlevel%
echo.
echo Built "%OUT%\CaptureFixtures.exe". Run it from the repo root:
echo   "%OUT%\CaptureFixtures.exe" --help
