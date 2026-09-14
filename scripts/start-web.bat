@echo off
REM ============================================================================
REM  DRGX launcher
REM
REM  Starts the CHS-DRG 3.0 grouping + regional payment web service.
REM  Pure batch: no PowerShell, no external tools.
REM
REM  One file, two layouts:
REM    packaged : DRGX.exe sits at the tree root (a release tree built by
REM               scripts\pack-release.bat, which also puts the plugins in a
REM               plugins\ folder next to it). Needs the ASP.NET Core 10 Runtime.
REM    dev      : no exe next to the data; falls back to
REM               `dotnet run --project src\DRGX.Host`, with the plugins still
REM               under artifacts\plugins. Needs the .NET 10 SDK.
REM
REM  Usage: double-click this file, or run it from a command prompt.
REM         The port is taken from appsettings.json (Drgx:Port) -- change it
REM         there, in one place, and it applies to this launcher and to a
REM         direct DRGX.exe run alike. For a one-off change:
REM           start-web.bat 9000        (first argument)
REM           set Drgx__Port=9000       (environment variable -- the same name
REM                                      the host itself reads)
REM ============================================================================

setlocal

REM Work from the tree root: the app resolves wwwroot and the data folders
REM relative to the current directory, and every root below is passed as an
REM ABSOLUTE path anyway so a launcher miscd cannot silently load nothing.
pushd "%~dp0.."
set "ROOT=%CD%"

REM ---- configuration --------------------------------------------------------
REM Port: ask the application instead of keeping a second copy of the default
REM here. `DRGX.exe --print-config` prints the configuration that is actually
REM in effect (appsettings.json / environment / built-in default), so editing
REM appsettings.json's Drgx:Port is enough for BOTH ways of starting: this
REM script reads that value and hands it straight back as --port.
REM Precedence for an override: first argument, then Drgx__Port, then what the
REM application resolved. The literal 8080 below is only a fallback for a
REM development tree, where there is no DRGX.exe to ask (dev runs `dotnet run`,
REM which reads the project's appsettings.json anyway).
REM stderr is deliberately NOT suppressed: a broken appsettings.json makes the
REM host print why, and that message belongs in this window, not in /dev/null.
REM
REM NO PIPE here, on purpose. A pipe inside `for /f`'s command string makes cmd
REM fail the whole line with "The filename, directory name, or volume label
REM syntax is incorrect" and leave PORT unset -- silently reinstating the second
REM copy of the default this block exists to remove. Matching the key inside the
REM loop body needs no filter command, so there is nothing to mis-escape.
REM NOTE: the key comparison must stay /i, and delims must stay "=" so that
REM "port=8080" splits into A=port, B=8080.
set "EXE=%ROOT%\DRGX.exe"
set "PORT="
if exist "%EXE%" for /f "usebackq tokens=1,2 delims==" %%A in (`"%EXE%" --print-config`) do if /i "%%A"=="port" set "PORT=%%B"
if defined PORT goto portResolved
REM A development tree has no DRGX.exe to ask; dev runs `dotnet run`, which reads
REM the project's own appsettings.json, so fall back quietly there.
if not exist "%EXE%" goto portDefault
echo.
echo [WARN] "%EXE%" --print-config did not report a port.
echo        appsettings.json is most likely unparsable. Run this to see why:
echo          "%EXE%" --print-config
echo.
:portDefault
set "PORT=8080"
:portResolved
if not "%Drgx__Port%"=="" set "PORT=%Drgx__Port%"
if not "%~1"=="" set "PORT=%~1"
echo %PORT%|findstr /r /c:"^[0-9][0-9]*$" >nul 2>nul
if errorlevel 1 goto badPort
set "PACK_DIR=%ROOT%\data\packs\chs-drg-3.0"
set "APP_DIR=%ROOT%\src\DRGX.Host"
set "REGIONS_DIR=%ROOT%\data\regions"
REM Plugin root differs by layout: a release tree flattens them next to the exe,
REM the repository keeps them under artifacts\. Pick whichever exists -- mirrors
REM the plugins -> artifacts/plugins probe in Program.cs, and passing it
REM explicitly keeps the choice visible instead of implicit.
set "PLUGINS_DIR=%ROOT%\plugins"
if not exist "%PLUGINS_DIR%" set "PLUGINS_DIR=%ROOT%\artifacts\plugins"
set "HIS_DIR=%ROOT%\configs\his"
REM Real credentials live in a git-ignored *.local.json; the committed *.sample.json
REM carries placeholders only, so the fallback is expected to fail to connect.
set "HIS_CFG=%HIS_DIR%\healthone-1.local.json"
if not exist "%HIS_CFG%" set "HIS_CFG=%HIS_DIR%\healthone-1.sample.json"
REM Region fee packs are auto-scanned from data\regions\<region>\<version>\ at
REM startup; the active region is picked in the web UI (no --region flag).
REM
REM NOTE: every root below is passed as an ABSOLUTE path on purpose. Under
REM   `dotnet run` the child process working directory becomes the PROJECT
REM   directory rather than this one, so relative paths would resolve under
REM   src\DRGX.Host\ and silently load nothing (symptom: the host logs "no plugins
REM   loaded" plus an empty region list). WebPaths falls back to ancestor lookup,
REM   so a manual `dotnet run` still works; passing absolute paths keeps it
REM   explicit either way.

REM ---- sanity checks --------------------------------------------------------
REM (goto style, not ( ) blocks: stays safe if the path contains & ( ) etc.)
if not exist "%PACK_DIR%" goto noPack

REM ---- port check ------------------------------------------------------------
REM Check the port BEFORE starting, because the wait loop below only looks at
REM netstat: with the port held by something else it would see LISTENING, declare
REM "Server is ready", open the browser and report success -- a false green that
REM points the operator at whatever process happens to own the port. The host
REM would have refused to start, but nobody would see why.
set "BUSY_PID="
for /f "tokens=5" %%P in ('netstat -ano 2^>nul ^| findstr /C:"LISTENING" ^| findstr /C:":%PORT% "') do set "BUSY_PID=%%P"
if defined BUSY_PID goto portBusy

REM Build the optional HIS argument now: append --his only when the file exists
REM (avoids a 503 HIS endpoint after manual edits).
set "EXTRA="
if exist "%HIS_CFG%" set "EXTRA=--his ""%HIS_CFG%"""

if exist "%EXE%" goto bannerPackaged
goto bannerDev

:bannerPackaged
set "MODE=packaged (DRGX.exe)"
goto banner

:bannerDev
set "MODE=development (dotnet run)"
if not exist "%APP_DIR%\DRGX.Host.csproj" goto noApp

:banner
echo Starting DRGX on port %PORT% ...
echo Mode      : %MODE%
echo Data pack : %PACK_DIR%
echo Regions   : %REGIONS_DIR% (auto-scanned; pick one in the web UI)
echo Plugins   : %PLUGINS_DIR%
echo HIS cfg   : %HIS_CFG% (enabled if the file exists)
echo URL       : http://localhost:%PORT%/
echo.

if exist "%EXE%" goto startPackaged
goto startDev

:startPackaged
start "DRGX" /min "%EXE%" --pack "%PACK_DIR%" --regions-root "%REGIONS_DIR%" --plugins-root "%PLUGINS_DIR%" --port %PORT% %EXTRA%
goto waitloop

:startDev
where dotnet >nul 2>nul
if errorlevel 1 goto noDotnet
start "DRGX" /min dotnet run --project "%APP_DIR%" -- --pack "%PACK_DIR%" --regions-root "%REGIONS_DIR%" --plugins-root "%PLUGINS_DIR%" --port %PORT% %EXTRA%
goto waitloop

REM ---- wait until the port is listening, then open the browser --------------
:waitloop
set tries=0

:waitspin
netstat -ano | findstr /C:"LISTENING" | findstr /C:":%PORT% " >nul 2>nul
if not errorlevel 1 goto opened

set /a tries+=1
if %tries% geq 120 goto timeout
REM Wait with ping rather than timeout: `timeout` fails instantly when stdin is
REM redirected (piped or automated launch), which would make this loop spin and
REM declare a timeout before the app ever had a chance to start.
ping -n 2 127.0.0.1 >nul 2>nul
goto waitspin

:opened
echo Server is ready. Opening browser ...
start "" http://localhost:%PORT%/
goto done

:badPort
echo [ERROR] Not a port number: "%PORT%"
echo         Give a plain number from 1 to 65535, for example:
echo           start-web.bat 9000
echo           set Drgx__Port=9000
echo         (Checked here so the message does not scroll away in the host
echo          window, which closes as soon as the host exits.)
pause
goto fail

:noPack
echo [ERROR] Data pack not found: %PACK_DIR%
echo         Run this script from the scripts\ folder of a complete tree.
pause
goto fail

:portBusy
echo [ERROR] Port %PORT% is already in use (PID %BUSY_PID%).
echo.
echo   DRGX is either already running, or another program holds the port.
echo     - Already running : just open http://localhost:%PORT%/
echo     - Other program   : use another port --  start-web.bat 9000
echo                         or   set Drgx__Port=9000   -- or stop it
echo     - Who is it       : tasklist /fi "PID eq %BUSY_PID%"
echo.
echo   (The server itself would refuse to start on a busy port; this check just
echo    makes sure you find out here instead of via an empty browser tab.)
pause
goto fail

:noApp
echo [ERROR] Neither DRGX.exe nor %APP_DIR%\DRGX.Host.csproj was found.
echo         The tree looks incomplete.
pause
goto fail

:noDotnet
echo [ERROR] The .NET SDK (dotnet) was not found on PATH, and there is no
echo         DRGX.exe at the tree root either.
echo         Install the ASP.NET Core 10 Runtime to use a release tree, or the
echo         .NET 10 SDK to run from source.
pause
goto fail

:timeout
echo [TIMEOUT] Server did not start within 120 seconds.
echo           Check the "DRGX" window for the real error.
echo           The usual cause on a fresh machine is a missing ASP.NET Core 10
echo           Runtime; DRGX.exe then exits immediately with a message telling
echo           you which runtime to install.
pause
goto fail

REM :done MUST exit explicitly. Labels fall through, so without its own exit this
REM block would run straight into :fail and report 1 on a successful start.
:done
echo.
echo DRGX is running. Close the "DRGX" window to stop it.
pause
popd
endlocal
exit /b 0

:fail
popd
endlocal
REM Non-zero so a script or shortcut can tell "refused to start" from "started".
exit /b 1
