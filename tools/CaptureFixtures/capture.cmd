@echo off
rem Builds CaptureFixtures, then runs it from the repo root, so the fixtures land in
rem blp-fixtures\ there. Every argument passes through, such as --day 2026-11-16.
setlocal
call "%~dp0build.cmd"
if errorlevel 1 exit /b %errorlevel%
pushd "%~dp0..\.."
"%LOCALAPPDATA%\blp-net\CaptureFixtures\CaptureFixtures.exe" %*
set "CODE=%errorlevel%"
popd
exit /b %CODE%
