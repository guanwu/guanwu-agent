@echo off
rem Copyright (C) 2026 Jiaowei <wei.jiao@guanwu.com>
rem SPDX-License-Identifier: AGPL-3.0-only

setlocal EnableExtensions

set "ROOT=%~dp0"
set "RID="
set "SKIP_TESTS="

:parse
if "%~1"=="" goto parsed
if /I "%~1"=="--skip-tests" (
  set "SKIP_TESTS=1"
  shift
  goto parse
)
if not defined RID (
  set "RID=%~1"
  shift
  goto parse
)
echo error: unexpected argument: %~1>&2
exit /b 2
:parsed

if defined RID goto rid_ready
if /I "%PROCESSOR_ARCHITECTURE%"=="ARM64" (
  set "RID=win-arm64"
) else (
  set "RID=win-x64"
)
:rid_ready

echo Building Guanwu Agent for %RID%

where dotnet >nul 2>nul
if errorlevel 1 (
  echo error: dotnet was not found on PATH>&2
  exit /b 1
)
where node >nul 2>nul
if errorlevel 1 (
  echo error: node was not found on PATH>&2
  exit /b 1
)
where npm >nul 2>nul
if errorlevel 1 (
  echo error: npm was not found on PATH>&2
  exit /b 1
)

rem Native AOT needs the MSVC linker. Locate the newest Visual Studio
rem installation with the matching C++ tools and import its environment.
set "VSDIR="
set "VSWHERE=%ProgramFiles(x86)%\Microsoft Visual Studio\Installer\vswhere.exe"
if not exist "%VSWHERE%" set "VSWHERE=%ProgramFiles%\Microsoft Visual Studio\Installer\vswhere.exe"
set "VCTOOLS=Microsoft.VisualStudio.Component.VC.Tools.x86.x64"
if /I "%RID%"=="win-arm64" set "VCTOOLS=Microsoft.VisualStudio.Component.VC.Tools.ARM64"
if not exist "%VSWHERE%" goto vs_done
set "VSLIST=%TEMP%\guanwu-agent-vswhere.txt"
"%VSWHERE%" -latest -products * -requires "%VCTOOLS%" -property installationPath > "%VSLIST%" 2>nul
if not exist "%VSLIST%" goto vs_done
set /p VSDIR=<"%VSLIST%"
del "%VSLIST%" >nul 2>nul
if not defined VSDIR goto vs_done

set "VCVARS=%VSDIR%\VC\Auxiliary\Build\vcvars64.bat"
if /I not "%RID%"=="win-arm64" goto vs_call
set "VCVARS=%VSDIR%\VC\Auxiliary\Build\vcvarsamd64_arm64.bat"
if exist "%VCVARS%" goto vs_call
set "VCVARS=%VSDIR%\VC\Auxiliary\Build\vcvarsarm64.bat"
:vs_call
if not exist "%VCVARS%" goto vs_done
call "%VCVARS%" >nul
set "IlcUseEnvironmentalTools=true"
:vs_done

if not defined IlcUseEnvironmentalTools echo warning: Visual Studio C++ tools were not found; Native AOT may fail.

pushd "%ROOT%"

if exist package-lock.json (
  call npm ci
) else (
  call npm install
)
if errorlevel 1 goto failed

call npm run typecheck
if errorlevel 1 goto failed

call npm run build
if errorlevel 1 goto failed

set "OUT=%ROOT%release\%RID%"
if exist "%OUT%" rmdir /s /q "%OUT%"
dotnet publish src\GuanwuAgent\GuanwuAgent.csproj -c Release -r "%RID%" -o "%OUT%"
if errorlevel 1 goto failed

rem Stage the test page and its SDK bundle under docs so the page resolves
rem dist/guanwu-agent.iife.min.js.
if exist "%OUT%\docs" rmdir /s /q "%OUT%\docs"
xcopy /e /i /y docs "%OUT%\docs" >nul
if errorlevel 1 goto failed
copy /y README.md "%OUT%\README.md" >nul
if errorlevel 1 goto failed
copy /y LICENSE "%OUT%\LICENSE" >nul
if errorlevel 1 goto failed

if defined SKIP_TESTS goto built

node tests\smoke.mjs "%OUT%\guanwu-agent.exe"
if errorlevel 1 goto failed

:built
popd
echo.
echo Built %OUT%
exit /b 0

:failed
popd
echo error: build failed>&2
exit /b 1
