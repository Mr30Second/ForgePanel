@echo off
setlocal
rem Build helper for ForgePanel. Double-click after "Rebuild all" would be needed:
rem it compiles the project and writes the full compiler output to build.log next to this file,
rem so the agent can read the log instead of asking for the output pane to be pasted.
rem ASCII only on purpose: cmd.exe reads .bat files in the OEM codepage and Russian text
rem in echo lines would print as garbage.

set "PROJ=%~dp0ForgePanel\ForgePanel.csproj"
set "LOG=%~dp0build.log"
set "VSWHERE=%ProgramFiles(x86)%\Microsoft Visual Studio\Installer\vswhere.exe"
set "MSBUILD="

if exist "%VSWHERE%" (
  for /f "usebackq delims=" %%p in (`"%VSWHERE%" -latest -prerelease -products * -requires Microsoft.Component.MSBuild -find "MSBuild\**\Bin\MSBuild.exe"`) do set "MSBUILD=%%p"
)

if not defined MSBUILD goto :dotnet

echo Using MSBuild from Visual Studio:
echo   %MSBUILD%
echo.
"%MSBUILD%" "%PROJ%" /t:Rebuild /v:m /nologo /fl /flp:logfile="%LOG%";verbosity=normal;encoding=UTF-8
set "RC=%ERRORLEVEL%"
goto :report

:dotnet
echo Visual Studio MSBuild not found, trying dotnet CLI...
echo.
dotnet build "%PROJ%" -t:Rebuild -v m --nologo -fl -flp:logfile="%LOG%";verbosity=normal
set "RC=%ERRORLEVEL%"

:report
echo.
if "%RC%"=="0" (echo BUILD SUCCEEDED, code 0) else (echo BUILD FAILED, code %RC%)
echo Full output written to:
echo   %LOG%
echo.
pause
