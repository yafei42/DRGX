@echo off
REM ============================================================================
REM  DRGX release packer
REM
REM  Publishes the host, lays out a minimal runnable release tree next to it, and
REM  zips the result.
REM
REM  Two goals: the tree must actually run, and the target machine must not have
REM  to be prepared. The default therefore ships a SELF-CONTAINED, trimmed,
REM  single-file exe -- nothing to install on the target machine. It is still
REM  small because nothing is compiled there and the bundle is compressed, not
REM  because the application was gutted.
REM
REM  Usage:
REM    pack-release.bat                    -> dist\drgx-<PKG_VER>-standalone\ (+ .zip)
REM    pack-release.bat /fdd               -> dist\drgx-<PKG_VER>-fdd\ (+ .zip)
REM                                           small package, needs ASP.NET Core 10
REM                                           Runtime on the target machine
REM    pack-release.bat D:\ship\drgx       -> D:\ship\drgx\
REM    pack-release.bat /nobuild           reuse the current artifacts\plugins
REM    pack-release.bat /nozip             tree only, skip the archive
REM    pack-release.bat /nosmoke           skip the post-pack smoke test
REM
REM  The output folder always carries the variant in its name. The two packages
REM  make different demands of the target machine (nothing vs a runtime install),
REM  so a tree that does not say which one it is invites shipping the wrong one.
REM  A positional path still overrides the whole folder name.
REM
REM  /standalone is accepted and means the default -- it used to be the opt-in
REM  flag, and silently ignoring it would hand a caller a package with a
REM  prerequisite while they believe they asked for none. Never let that flag
REM  change meaning again.
REM
REM  VERSIONS -- three labels, deliberately different, do not conflate them
REM    program version   1.1.0
REM        Names the release folder and its archive -- dist\drgx-1.1.0-standalone\
REM        or dist\drgx-1.1.0-fdd\, plus the .zip next to each.
REM        Declared here as PKG_VER, mirrored in package.json, and pinned for
REM        the assemblies in Directory.Build.props. Bump it whenever the
REM        software changes -- it tracks the code, not the data.
REM    assembly version  1.0.0.0
REM        The plugin binding identity (AssemblyVersion). Pinned separately in
REM        Directory.Build.props so that bumping the program version does NOT
REM        invalidate every already-compiled plugin. Only a breaking API change
REM        should move it, and then the plugins must be rebuilt with it.
REM    scheme version    3.0 (batch 2026-09-09)
REM        The CHS-DRG grouping scheme, from data\packs\<pack>\manifest.json.
REM        The batch date is manifest.sourceDate, and that is what the UI and the
REM        README show. They deliberately do NOT show manifest.revision: that is
REM        our own data-package revision number and moves with our ingestion
REM        changes, so surfacing it reads as "the NHSA has published N versions"
REM        when it has published one. Never derived from PKG_VER.
REM
REM  WHAT THE TREE CONTAINS
REM    DRGX.exe               the published host at the tree root, next to
REM                           wwwroot\ and the data folders.
REM                           Default: a self-contained single-file bundle that
REM                           carries the whole runtime inside it. There is no
REM                           DRGX.dll / .deps.json / .runtimeconfig.json beside
REM                           it -- if you can see those, the publish fell back
REM                           to framework-dependent and the tree DOES have a
REM                           prerequisite. Step 3 checks the exe size to catch
REM                           exactly that.
REM                           /fdd: a 162 KB apphost plus DRGX.dll and those two
REM                           json files, because the runtime is expected to be
REM                           installed on the target machine instead.
REM    appsettings.json       runtime configuration: port, listen address,
REM                           data/plugin paths, log levels. Copied by the
REM                           publish; the operator edits it in place, no
REM                           rebuild needed. The code carries the same values
REM                           as built-in defaults, so a tree without it still
REM                           runs -- it is checked for below anyway, because a
REM                           missing config file is a silent behaviour change.
REM    plugins\               the three plugins, flat next to the exe. There is
REM                           no artifacts\ layer in a release tree.
REM    data\                  CHS-DRG data pack + regional fee packs.
REM    configs\               HIS connection config (*.local.json never ships).
REM    scripts\start-web.bat  the launcher.
REM    README.md              how to start it, how to enable HIS, what the exit
REM                           codes mean. Hand-written, copied from the repo
REM                           root so the operator never has to reverse-engineer
REM                           a folder full of dlls.
REM    LICENSE  NOTICE        licenses.
REM
REM  WHAT IS DELIBERATELY ABSENT
REM    src\                   the host runs from the published DLLs. Shipping the
REM                           sources would only invite a rebuild on the target.
REM    bin\ obj\              MSBuild intermediates.
REM    *.pdb                  debug symbols. Shipping them costs several MB and
REM                           buys nothing on a machine that has no debugger
REM                           attached; keep a symbol copy elsewhere if a crash
REM                           dump ever needs readable stack frames.
REM    DRGX.slnx, Directory.Build.props, .editorconfig, .gitattributes,
REM    .gitignore, package.json
REM                           build-time only, nothing reads them at runtime.
REM    docs\                  exists but is empty. The operator-facing document
REM                           is README.md at the tree root, not this folder.
REM    .workbuddy\  dist\     local scratch, and this script's own output.
REM    tmp\ testdata\         probe scratch and large derived fixtures.
REM    *.local.json *.secret.json      real HIS connection strings
REM                                    (*.sample.json IS kept).
REM    *.out.jsonl            batch grouping output.
REM    hqmsts*.xlsx           large derived test fixtures.
REM
REM  BUILDING FROM SOURCE -- repo-side knowledge only. README.md is the
REM  operator-facing document and deliberately carries no build steps (the tree
REM  has no src\ and no SDK is needed to run it), so the commands live here:
REM    dotnet build DRGX.slnx -c Release
REM    dotnet run --project src\DRGX.Host -- --port 8090
REM        Explicit port on purpose: on this workstation 8080 is routinely held
REM        by another service, so a dev run would otherwise fail to bind for a
REM        reason that has nothing to do with the change being tested.
REM    Needs the .NET 10 SDK; every project here targets net10.0. There is no
REM    test project -- verify changes by running the host and hitting it.
REM
REM  TRIMMED INSIDE THE TREE (runtime assets only, no code change):
REM    plugins\DRGX.His.SqlServer\runtimes\{unix,win-arm64,win-x86}
REM                     the host runs win-x64; those RIDs are never resolved
REM                     (runtimes\win and runtimes\win-x64 are kept)
REM    ...\DRGX.His.SqlServer\{cs,de,es,fr,it,ja,ko,pl,pt-BR,ru,tr}
REM                     SqlClient satellite cultures. zh-Hans / zh-Hant are kept
REM                     and everything else falls back to the neutral resource.
REM
REM  TWO VARIANTS, TWO DIFFERENT TARGET MACHINES. The variant is in the output
REM  folder name, so a tree can never be mistaken for the other one.
REM
REM  DEFAULT - self-contained + trimmed single file, about 32.5 MB zip, NO
REM  prerequisite.
REM    Nothing to install on the target machine. This is the default because
REM    "copy the folder and run it" is the only deployment story that survives
REM    contact with a hospital workstation that has no admin rights.
REM    * exe size, measured 2026-09-14, same tree, same flags except as noted:
REM        framework-dependent apphost           162,304 B
REM        self-contained, compressed          50,289,735 B   (~48 MB)
REM        self-contained, compressed, TRIMMED 22,157,257 B   (~21 MB)  <- here
REM        self-contained, uncompressed       103,803,364 B   (~99 MB)
REM      Compression buys 53 MB and trimming buys another 28 MB. Keep both;
REM      dropping either is a 25-50 MB regression for nothing.
REM    * Package totals for the two shipped variants:
REM        /fdd     549 files / 26.4 MiB tree / 16.7 MiB zip
REM        default  541 files / 47.1 MiB tree / 32.5 MiB zip
REM      Roughly 90% of the default tree is payload (fonts 9.5 MB, the HIS
REM      plugin 9 MB, the data pack 5.5 MB). No packaging change moves that
REM      much, so do not chase it below these numbers.
REM    * PublishTrimmed by itself does NOT break the plugins. Measured: all 3
REM      load, and all 26 probed endpoints answer byte-identically to the
REM      framework-dependent build.
REM    * Two things ARE required for trimming to be safe, and both fail
REM      silently if dropped -- see the "standalone notes" below the markers.
REM    * Verify any change to this path before shipping:
REM        python .workbuddy\verify-trim.py <reference tree> <standalone tree>
REM
REM  /fdd - framework-dependent, about 16.7 MB zip.
REM    Use it only when the target machine definitely has, or may have, the
REM    ASP.NET Core 10 **Runtime** (not the SDK) installed and you want the
REM    smaller package. A missing runtime shows up as exit code 150 plus an
REM    English hostfxr message; start-web.bat is what turns that into a
REM    readable hint.
REM
REM  --- standalone notes: the two silent prerequisites of trimming ---
REM    1. JSON. Trimming leaves the HttpJsonOptions resolver chain EMPTY (a
REM       non-null empty combinator, which is why `??=` does not fix it), and
REM       then EVERY request 500s -- including / and /style.css -- because the
REM       failure happens while the endpoint data source is being built.
REM       ApiJson.Apply puts DefaultJsonTypeInfoResolver back on the chain.
REM    2. PLUGIN FRAMEWORK ASSEMBLIES. Plugins are loaded at runtime from
REM       plugins\, so the trimmer cannot see which FRAMEWORK types they need
REM       and removes them. The plugins still report "loaded" 3/3, and the
REM       failure only fires at the moment such a type is used -- a
REM       TypeLoadException whose Message is EMPTY, e.g. the HIS connector
REM       failing at construction. DRGX.Host.csproj imports
REM       plugin-framework-roots.props (112 TrimmerRootAssembly entries) to
REM       keep the transitive closure. That file is GENERATED -- regenerate it
REM       after adding a plugin or a framework dependency to one:
REM         dotnet run .workbuddy\plugin-framework-refs.cs artifacts\plugins ^
REM           -o src\DRGX.Host\plugin-framework-roots.props
REM    A full clone of the runtime is kept rather than a hand-tuned minimal
REM    set: the closure costs ~2.6 MB more than bare trimming, while getting it
REM    wrong costs a runtime crash that only shows up in production.
REM
REM  Encoding: pure ASCII, CRLF, no BOM, no chcp. Please keep it that way.
REM ============================================================================

setlocal

REM This script's own directory, captured up front -- see the note in "resolve
REM roots" about why it cannot be read as %~dp0 after the argument loop.
set "SCRIPT_DIR=%~dp0"

REM ---- build environment hygiene -------------------------------------------
REM MSBuild imports environment variables as properties, and Version is one of
REM them. A caller with VERSION or VersionPrefix set therefore changes the
REM AssemblyVersion of every assembly built here, silently.
REM
REM Proved 2026-09-14: this script used to keep its own package number in a
REM variable called VERSION. MSBuild read it as $(Version), so the release build
REM stamped the shared assemblies 3.0.0.0 and the plugin assemblies recorded a
REM reference to "DRGX.Fee, Version=3.0.0.0". start-web.bat then built the same
REM sources as 1.0.0.0, every plugin failed to bind its shared assembly, and the
REM host printed "plugins: 0" and still exited 0 -- a broken package that looks
REM fine. The name is PKG_VER now, and the variables are cleared outright so a
REM poisoned caller environment cannot reach the build either.
REM
REM Second line of defence: Directory.Build.props now states Version and
REM AssemblyVersion explicitly, and an explicit property in a project file wins
REM over the same name imported from the environment. A stray VERSION in the
REM caller's shell therefore cannot change what this build stamps any more.
for %%V in (VERSION VersionPrefix VersionSuffix ASSEMBLYVERSION FILEVERSION INFORMATIONALVERSION) do set "%%V="

REM ---- configuration --------------------------------------------------------
REM The package label. It names dist\drgx-<PKG_VER>-<variant>\ and the .zip next
REM to it -- both derive from this one value, so they can never disagree.
REM
REM This is the PROGRAM version. It is NOT the CHS-DRG scheme version: that one
REM is 3.0, it lives in data\packs\<pack>\manifest.json, and it is what the UI
REM shows (via /api/info). Keep this in step with package.json. The assemblies
REM carry a version of their own, declared in Directory.Build.props.
set "PKG_VER=1.1.0"
set "SKIP_BUILD="
set "NO_ZIP="
set "NO_SMOKE="
REM One bit, and its absence is the default: the standalone bundle. Set it to ask
REM for the framework-dependent package instead. Do not add a second variable for
REM the same choice -- the old script had the flag inverted (STANDALONE opt-in)
REM and every branch had to be read twice to be sure which way it went.
set "FDD="
set "OUT="

:parse
if "%~1"=="" goto parsed
if /i "%~1"=="/nobuild" goto opt_nobuild
if /i "%~1"=="/nosmoke" goto opt_nosmoke
if /i "%~1"=="/nozip" goto opt_nozip
if /i "%~1"=="/fdd" goto opt_fdd
if /i "%~1"=="/standalone" goto opt_standalone
set "OUT=%~1"
goto next_arg
:opt_nobuild
set "SKIP_BUILD=1"
goto next_arg
:opt_nosmoke
set "NO_SMOKE=1"
goto next_arg

:opt_nozip
set "NO_ZIP=1"
goto next_arg

REM The old opt-in spelling. It now names what the default already does; keeping
REM it a no-op is the point -- a caller who asks for "no prerequisite" and gets a
REM package WITH one has a package that installs nothing and then fails at the
REM customer site, which is far worse than an ignored argument.
:opt_standalone
set "FDD="
goto next_arg

:opt_fdd
set "FDD=1"
goto next_arg
:next_arg
shift
goto parse
:parsed

REM ---- resolve roots --------------------------------------------------------
REM pushd, not %~dp0.. : keeps ROOT free of a trailing "..\" segment.
REM
REM SCRIPT_DIR must be captured before the argument loop, not read here: `shift`
REM renumbers the positional parameters, so after one shift %0 is the *first
REM argument* and %~dp0 describes its directory instead of this script's. With a
REM positional output path the script then pushed into that path's parent and
REM reported "DRGX.slnx not found" for a perfectly good repository -- and only
REM when an argument was passed, since the loop never shifts on a bare run.
pushd "%SCRIPT_DIR%.."
set "ROOT=%CD%"

REM The variant always shows up in the leaf name. The two packages make different
REM demands of the target machine (runtime install vs none), so a folder that
REM does not say which one it is invites shipping the wrong one -- and the two
REM used to share the name dist\drgx-<PKG_VER>, which is exactly how the mix-up
REM would have happened. The leaf name also becomes the .zip name.
if "%OUT%"=="" if defined FDD set "OUT=%ROOT%\dist\drgx-%PKG_VER%-fdd"
if "%OUT%"=="" set "OUT=%ROOT%\dist\drgx-%PKG_VER%-standalone"

REM ---- sanity checks --------------------------------------------------------
REM (goto style, not ( ) blocks: stays safe if the path contains & ( ) etc.)
if not exist "%ROOT%\DRGX.slnx" goto no_root
if not exist "%ROOT%\src\DRGX.Host\DRGX.Host.csproj" goto no_host
REM /nobuild reuses the plugin output already on disk, so it must exist.
if defined SKIP_BUILD if not exist "%ROOT%\artifacts\plugins" goto no_plugins
REM dotnet is needed either way: the plugin build, and the host publish.
where dotnet >nul 2>nul
if errorlevel 1 goto no_dotnet
echo.
echo DRGX release packer
echo   source : %ROOT%
echo   output : %OUT%
echo.

REM ---- step 1: wipe stale build state --------------------------------------
REM Two separate traps make a merely incremental build unsafe to package.
REM
REM 1. artifacts\plugins is a build output that dotnet publish never touches:
REM    the host does not reference the plugin projects, it loads them at runtime
REM    by scanning the plugins root. So without an explicit plugin build the
REM    package ships whatever happened to be in that folder last time.
REM 2. obj\ holds generated files MSBuild does not always regenerate -- notably
REM    AssemblyInfo.cs. A plugin compiled against one version of a shared
REM    assembly cannot bind against another version (see the environment note
REM    above), so one stale obj\ is enough to ship a package whose plugins all
REM    fail to load. Wiping it costs one full build and removes that whole
REM    class of problem, which is why obj\ is not trusted here.
REM
REM The failure is SILENT either way: the host starts, logs a warning, reports
REM "plugins: 0" and still exits 0. Only the smoke test below catches it.
echo [1/6] Clearing build intermediates and plugin output...
REM /nobuild means "reuse the plugin output already on disk", so this step must
REM not run: wiping artifacts\plugins here and then skipping the build leaves the
REM packer with nothing to copy (and it deleted the very folder the /nobuild
REM sanity check above had just validated).
if defined SKIP_BUILD goto keep_state
for /f "delims=" %%D in ('dir /s /b /ad "%ROOT%\src" 2^>nul ^| findstr /i /c:"\obj"') do rd /s /q "%%D" 2>nul
if exist "%ROOT%\artifacts\plugins" rd /s /q "%ROOT%\artifacts\plugins"
goto state_cleared
:keep_state
echo   skipped (/nobuild) - obj\ and artifacts\plugins are left as they are
:state_cleared

REM ---- step 2: build the plugins -------------------------------------------
echo [2/6] Building solution (Release)...
if not defined SKIP_BUILD goto do_build
echo   skipped (/nobuild) - artifacts\plugins is used as-is
goto after_build
:do_build
dotnet build "%ROOT%\DRGX.slnx" -c Release -v minimal
if errorlevel 1 goto build_failed
:after_build

REM ---- step 3: publish the host --------------------------------------------
REM Default is the self-contained, trimmed, single-file bundle: the target machine
REM installs nothing at all. /fdd switches to framework-dependent (no -r, no
REM --self-contained), which is smaller but needs the runtime on the target.
REM Either way the publish output IS the release root, so DRGX.exe and the data
REM folders sit side by side and the launcher needs no extra path indirection.
REM Header has the measured sizes and the two silent prerequisites of trimming.
if defined FDD goto pub_label_fdd
echo [3/6] Publishing the host (standalone: self-contained + trimmed)...
goto pub_dir
:pub_label_fdd
echo [3/6] Publishing the host (framework-dependent, /fdd)...
:pub_dir
if not exist "%OUT%" goto make_out
rd /s /q "%OUT%" 2>nul
if exist "%OUT%" goto out_locked
:make_out
mkdir "%OUT%" 2>nul
if not exist "%OUT%" goto out_failed

if defined FDD goto pub_run_fdd
dotnet publish "%ROOT%\src\DRGX.Host\DRGX.Host.csproj" -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:PublishTrimmed=true -p:TrimMode=partial -p:EnableCompressionInSingleFile=true -o "%OUT%" -v minimal
set "PUB_RC=%ERRORLEVEL%"
goto pub_check

:pub_run_fdd
dotnet publish "%ROOT%\src\DRGX.Host\DRGX.Host.csproj" -c Release -o "%OUT%" -v minimal
set "PUB_RC=%ERRORLEVEL%"

:pub_check
if not "%PUB_RC%"=="0" goto publish_failed
if not exist "%OUT%\DRGX.exe" goto no_exe
if not exist "%OUT%\wwwroot\index.html" goto no_wwwroot
if not exist "%OUT%\appsettings.json" goto no_appsettings
REM The standalone tree MUST really be a bundle. A framework-dependent apphost is
REM ~162 KB; the trimmed bundle is ~21 MB, so 5 MB separates them by a mile.
REM Without this check a silently ignored property would ship a package that
REM advertises "no prerequisites" and in fact needs the runtime -- it installs
REM nothing, then fails at the customer site. That is the one failure this
REM variant exists to prevent, so it is checked rather than assumed.
REM (Note the guard is the INVERSE of the branch above: absence of FDD is the
REM standalone case. Keep it that way if the default ever flips again.)
if defined FDD goto pub_size_ok
for %%F in ("%OUT%\DRGX.exe") do if %%~zF LSS 5000000 goto not_bundled
:pub_size_ok

REM ---- step 4: copy the payload the publish does not cover ------------------
REM robocopy exit codes 0-7 mean success (1 = files copied). 8+ is a real error.
REM The whole repo is NOT copied any more, so "dist" no longer needs excluding.
REM Plugins are flattened to plugins\ on purpose: a release tree should not make
REM the operator reason about an artifacts\ level that only exists for the build.
echo [4/6] Copying data, configs, plugins and the launcher...
robocopy "%ROOT%\data" "%OUT%\data" /E /XF *.local.json *.secret.json *.out.jsonl hqmsts*.xlsx /NFL /NDL /NJH /NJS /NP
if %ERRORLEVEL% GEQ 8 goto copy_failed
robocopy "%ROOT%\configs" "%OUT%\configs" /E /XF *.local.json *.secret.json /NFL /NDL /NJH /NJS /NP
if %ERRORLEVEL% GEQ 8 goto copy_failed
robocopy "%ROOT%\artifacts\plugins" "%OUT%\plugins" /E /NFL /NDL /NJH /NJS /NP
if %ERRORLEVEL% GEQ 8 goto copy_failed
if not exist "%OUT%\plugins" goto no_plugins_copied
mkdir "%OUT%\scripts" 2>nul
copy /y "%ROOT%\scripts\start-web.bat" "%OUT%\scripts\start-web.bat" >nul
copy /y "%ROOT%\LICENSE" "%OUT%\LICENSE" >nul
copy /y "%ROOT%\NOTICE" "%OUT%\NOTICE" >nul
REM Checked before copying rather than assumed: a tree shipped without its
REM README still starts and still serves, so nothing downstream would ever
REM complain -- the operator is simply left with appsettings.json and a batch
REM file to reverse-engineer. A missing doc is silent, so it is a check.
if not exist "%ROOT%\README.md" goto no_readme
copy /y "%ROOT%\README.md" "%OUT%\README.md" >nul
REM The README embeds screenshots, so the tree needs their folder as well. Same
REM reasoning as above: README.md without docs\images\ still opens and still
REM reads fine -- only the images go silently missing, which nobody reports.
if not exist "%ROOT%\docs\images" goto no_docs_images
robocopy "%ROOT%\docs\images" "%OUT%\docs\images" /E /NFL /NDL /NJH /NJS /NP
if %ERRORLEVEL% GEQ 8 goto copy_failed

REM ---- step 5: strip symbols, trim plugin runtime assets --------------------
echo [5/6] Stripping debug symbols and trimming plugin assets...
set "PDB=0"
for /r "%OUT%" %%F in (*.pdb) do set /a PDB+=1
if %PDB% EQU 0 goto after_pdb
for /r "%OUT%" %%F in (*.pdb) do del /q "%%F" 2>nul
echo   removed %PDB% pdb files

:after_pdb
set "SLIM=%OUT%\plugins\DRGX.His.SqlServer"
if not exist "%SLIM%" goto no_slim

for %%C in (cs de es fr it ja ko pl pt-BR ru tr) do if exist "%SLIM%\%%C" rd /s /q "%SLIM%\%%C" 2>nul
if exist "%SLIM%\runtimes\unix" rd /s /q "%SLIM%\runtimes\unix" 2>nul
if exist "%SLIM%\runtimes\win-arm64" rd /s /q "%SLIM%\runtimes\win-arm64" 2>nul
if exist "%SLIM%\runtimes\win-x86" rd /s /q "%SLIM%\runtimes\win-x86" 2>nul
echo   dropped non-win-x64 RIDs and non-Chinese satellite cultures
goto plugin_check

:no_slim
echo   skipped - DRGX.His.SqlServer plugin not present

:plugin_check
REM ---- verify the plugin artifacts ------------------------------------------
REM A plugin folder without its entry assembly or .deps.json loads as "no
REM plugin" without failing, so catch it before the tree is shipped.
set "PLUGIN_GOOD=0"
set "PLUGIN_BAD="
for /d %%P in ("%OUT%\plugins\*") do set /a PLUGIN_GOOD+=1
for /d %%P in ("%OUT%\plugins\*") do if not exist "%%~P\%%~nxP.dll" set "PLUGIN_BAD=%%~nxP"
for /d %%P in ("%OUT%\plugins\*") do if not exist "%%~P\%%~nxP.deps.json" set "PLUGIN_BAD=%%~nxP"
if not defined PLUGIN_BAD goto plugin_check_ok
echo   ERROR: plugin folder is missing its entry assembly or deps file: %PLUGIN_BAD%
set "SMOKE_VERDICT=failed"
goto report

:plugin_check_ok
echo   %PLUGIN_GOOD% plugin folders look complete


REM ---- step 6: smoke test ---------------------------------------------------
REM A stale or mixed plugin set does not fail loudly -- the host starts, warns,
REM reports 0 plugins and exits 0. So the packaged tree is started once here and
REM the verdict is read from the log rather than assumed.
REM Pass /nosmoke to skip.
if defined NO_SMOKE goto after_smoke
if not exist "%OUT%\DRGX.exe" goto after_smoke
if not exist "%OUT%\plugins" goto after_smoke

echo [6/6] Smoke test (start the packaged host, confirm the plugins load)...
set "SMOKE_PORT=7099"
set "SMOKE_LOG=%TEMP%\drgx-smoke.log"
set "SMOKE_BAT=%TEMP%\drgx-smoke.bat"
if exist "%SMOKE_LOG%" del /q "%SMOKE_LOG%" 2>nul

> "%SMOKE_BAT%" echo @echo off
>> "%SMOKE_BAT%" echo cd /d "%OUT%"
>> "%SMOKE_BAT%" echo "%OUT%\DRGX.exe" --pack "%OUT%\data\packs\chs-drg-3.0" --regions-root "%OUT%\data\regions" --plugins-root "%OUT%\plugins" --port %SMOKE_PORT% ^> "%SMOKE_LOG%" 2^>^&1
start "DRGX smoke test" /min "%SMOKE_BAT%"

REM Both markers are deliberately pure ASCII: the host log is localised, so
REM matching Chinese text would break under another console code page.
REM "direct-payment" only shows up once the fee algorithm plugins registered.
set /a smoke_wait=0
:smoke_wait
findstr /c:"Could not load file or assembly" "%SMOKE_LOG%" >nul 2>nul && goto smoke_failed
findstr /c:"direct-payment" "%SMOKE_LOG%" >nul 2>nul && goto smoke_passed
set /a smoke_wait+=1
if %smoke_wait% geq 60 goto smoke_timedout
ping -n 2 127.0.0.1 >nul
goto smoke_wait

:smoke_passed
echo   OK - the packaged tree loads its plugins
set "SMOKE_VERDICT=ok"
goto smoke_stop

:smoke_failed
echo   FAILED - the packaged tree cannot load its plugins
set "SMOKE_VERDICT=failed"
goto smoke_stop

:smoke_timedout
echo   WARNING - no verdict within 60s, inspect %SMOKE_LOG%
set "SMOKE_VERDICT=timeout"

:smoke_stop
REM The packaged tree is only started here, never built, so there is no bin\
REM or obj\ to clean up afterwards.
taskkill /f /t /fi "WINDOWTITLE eq DRGX smoke test*" >nul 2>nul
for /f "tokens=5" %%P in ('netstat -ano 2^>nul ^| findstr ":%SMOKE_PORT%" ^| findstr "LISTENING"') do taskkill /f /pid %%P >nul 2>nul

:after_smoke
REM ---- report ---------------------------------------------------------------
:report
echo.
echo ---- release tree ---------------------------------------------------------
REM Count with for /r instead of parsing dir output: dir is localised
REM and would need Chinese/English keyword matching.
set "PKG_FILES=0"
set "PKG_BYTES=0"
for /r "%OUT%" %%F in (*) do set /a PKG_FILES+=1, PKG_BYTES+=%%~zF
set /a PKG_MB=(PKG_BYTES+524288)/1048576
echo   files : %PKG_FILES%
echo   size  : %PKG_BYTES% bytes  (about %PKG_MB% MB)
echo ---------------------------------------------------------------------------
echo   %OUT%
echo.
echo   Run scripts\start-web.bat, or DRGX.exe directly from the tree root.
echo   README.md at the tree root covers startup, HIS setup and the exit codes.
REM The hint must follow the variant, not a flag that may not have been passed:
REM telling an operator to install a runtime the package already contains (or
REM omitting it for the /fdd build) is a support call waiting to happen.
if defined FDD goto report_needs_runtime
echo   Self-contained: the target machine needs NOTHING installed.
goto after_report_hint
:report_needs_runtime
echo   Needs the ASP.NET Core 10 Runtime (not the SDK) on the target machine.
:after_report_hint

REM ---- optional archive -----------------------------------------------------
REM Two traps here, both silent-ish:
REM   1. `where tar` can find a NON-Windows tar. Started from Git Bash this picks
REM      up /usr/bin/tar (GNU), which reads the "D:" of an absolute path as a
REM      remote host -- "Cannot connect to D: resolve failed" -- and cannot
REM      write zip at all (its -a only does gzip/bzip2/xz).
REM   2. Even GNU aside, an absolute -f argument is what triggers trap 1.
REM So: resolve the Windows bsdtar by full path, and give it RELATIVE paths by
REM cd-ing to the parent first, so no drive letter ever reaches the arguments.
if defined NO_ZIP goto done
set "TAREXE=%SystemRoot%\System32\tar.exe"
if not exist "%TAREXE%" set "TAREXE="
if not defined TAREXE for %%I in (tar.exe) do set "TAREXE=%%~$PATH:I"
if not defined TAREXE goto no_tar
for %%I in ("%OUT%") do set "OUTNAME=%%~nxI"
set "ZIP=%OUT%.zip"
if exist "%ZIP%" del /q "%ZIP%" 2>nul
echo.
echo Packaging %ZIP% ...
pushd "%OUT%\.."
"%TAREXE%" -a -c -f "%OUTNAME%.zip" -C "%OUTNAME%" .
set "ZIP_RC=%ERRORLEVEL%"
popd
if not "%ZIP_RC%"=="0" goto zip_failed
echo   zip written: %ZIP%
goto done

:no_tar
echo.
echo   NOTE: tar.exe not found, tree left unpacked. Zip the folder by hand.
goto done

:zip_failed
echo.
echo   WARNING: archive step failed. The tree itself is complete and usable.
goto done

:done
echo.
echo Done.
popd
if "%SMOKE_VERDICT%"=="failed" exit /b 1
exit /b 0

REM ---- failures -------------------------------------------------------------
:no_root
echo ERROR: DRGX.slnx not found under "%ROOT%".
echo        Run this script from the scripts\ folder of the repository.
goto fail

:no_host
echo ERROR: src\DRGX.Host\DRGX.Host.csproj not found under "%ROOT%".
goto fail

:no_plugins
echo ERROR: "%ROOT%\artifacts\plugins" is missing.
echo        Build the plugin projects first, or drop the /nobuild flag.
goto fail

:no_dotnet
echo ERROR: dotnet not found on PATH. Install the .NET 10 SDK.
goto fail

:build_failed
echo ERROR: dotnet build failed. Fix the build before packaging.
goto fail

:publish_failed
echo ERROR: dotnet publish failed. Fix the build before packaging.
goto fail

:no_exe
echo ERROR: the publish produced no DRGX.exe under "%OUT%".
echo        The exe name comes from AssemblyName in DRGX.Host.csproj; if that
echo        changes, update this script.
goto fail

:not_bundled
echo ERROR: the standalone publish produced a small DRGX.exe (under 5 MB), which
echo        means it fell back to framework-dependent. The tree would need the
echo        ASP.NET Core runtime installed, so do NOT ship it as standalone.
echo        Check the publish command in step 3 against the header notes.
goto fail

:no_wwwroot
echo ERROR: the publish produced no wwwroot\index.html under "%OUT%".
echo        The front end would come up blank; check the project's static assets.
goto fail

:no_appsettings
echo ERROR: the publish produced no appsettings.json under "%OUT%".
echo        The tree would still run, but on built-in defaults and with no file
echo        for the operator to edit -- add it back to DRGX.Host.csproj or check
echo        that it is still at src\DRGX.Host\appsettings.json.
goto fail

:no_readme
echo ERROR: README.md is missing from the repository root, so the tree would
echo        ship with no operator-facing documentation at all.
echo        It is hand-written (nothing generates it) -- restore it and re-run.
goto fail

:no_docs_images
echo ERROR: docs\images\ is missing from the repository root. README.md embeds
echo        screenshots from there, so the tree would ship a doc whose images
echo        all render as broken boxes. Restore the folder and re-run.
goto fail

:no_plugins_copied
echo ERROR: no plugin folder reached "%OUT%\plugins".
goto fail

:out_locked
echo ERROR: could not remove "%OUT%". A process may still be using it.
goto fail

:out_failed
echo ERROR: could not create "%OUT%". Check the path and permissions.
goto fail

:copy_failed
echo ERROR: robocopy failed with code %ERRORLEVEL%. See the messages above.
goto fail

:fail
popd
exit /b 1
