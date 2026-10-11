@echo off
rem Starts ug on an installed .NET 8+ runtime, for cmd and PowerShell (hooks use bin/ug).
rem When ug can't start it prints one line to stderr and exits 0 (design rule 9).
setlocal

set "root=%~dp0.."
set "dll=%root%\dist\ug\ug.dll"
if not exist "%dll%" (
    echo ungrep: ug is not built; run: dotnet publish "%root%\src\Ungrep.Cli" -c Release -o "%root%\dist\ug" 1>&2
    exit /b 0
)

rem DOTNET_ROOT may point at Unity's bundled 6.0 runtime; :check skips it then.
set "found="
if defined DOTNET_ROOT call :check "%DOTNET_ROOT%"
if not defined found for /f "delims=" %%P in ('where dotnet 2^>nul') do if not defined found call :check "%%~dpP."
if not defined found call :check "%ProgramFiles%\dotnet"
if not defined found call :check "%LOCALAPPDATA%\Microsoft\dotnet"
if not defined found (
    echo ungrep: needs the .NET 8 runtime or newer: https://dotnet.microsoft.com/download/dotnet/8.0 1>&2
    exit /b 0
)

"%found%\dotnet.exe" "%dll%" %*
exit /b %errorlevel%

rem Sets found=%1 when it holds dotnet.exe and a Microsoft.NETCore.App runtime of major 8 or newer.
:check
if not exist "%~f1\dotnet.exe" exit /b 0
for /d %%V in ("%~f1\shared\Microsoft.NETCore.App\*") do (
    for /f "delims=." %%M in ("%%~nxV") do if %%M GEQ 8 set "found=%~f1"
)
exit /b 0
