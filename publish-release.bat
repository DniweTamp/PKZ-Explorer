@echo off
rem ============================================================================
rem  PKZ Explorer - Release publish (framework-dependent single file, win-x64)
rem
rem  Mirrors the Visual Studio publish profile "FolderProfile":
rem    Profile name            FolderProfile
rem    Configuration           Release | Any CPU
rem    Target framework        net8.0-windows
rem    Deployment mode         Framework-dependent
rem    Target runtime          win-x64
rem    Target location         bin\Release\net8.0-windows\publish\win-x64\
rem    Produce single file     ON
rem    Enable ReadyToRun       OFF
rem
rem  Usage:
rem    publish-release.bat                     publish, then write src.zip and
rem                                            PkzExplorer-win-x64.zip
rem    publish-release.bat /noarchives         publish only, skip both archives
rem    publish-release.bat /nopause            do not wait for a keypress at the end
rem
rem  Switches can be combined, e.g.  publish-release.bat /noarchives /nopause
rem
rem  Artifacts written beside this script:
rem    src.zip                     the tracked files at HEAD (git archive)
rem    PkzExplorer-win-x64.zip     the published build folder
rem
rem  Notes:
rem    * Symbols (.pdb) are stripped from the publish output by the project, so the folder ships
rem      only the executable and the .NET runtime's own files.
rem    * The .NET 8 Desktop Runtime (x64) must be installed on the target machine; this is a
rem      framework-dependent build, and WPF's native components come from that runtime.
rem    * The Oodle dll (oo2core_*.dll) is NOT bundled. Oodle is proprietary, so the user points
rem      Settings at their own copy; without one, Oodle entries cannot be read or written but
rem      None and ZStandard archives work normally.
rem    * The tool keeps settings.json beside the executable, so it stays portable. A publish
rem      folder you have been running from therefore holds your settings; the output folder is
rem      removed on every run, so copy settings.json out first if you care about it.
rem    * src.zip archives HEAD, so uncommitted work is not in it. That is deliberate: the archive
rem      should describe a build that exists in the history. The script says so when the tree is
rem      dirty rather than silently shipping a zip that does not match the binary.
rem    * PkzTool shares types with XT1 Studio, so publishing needs that checkout beside this one,
rem      or -p:Xt1CoreProject=<path> pointing at its Xt1.Core.csproj.
rem ============================================================================

setlocal EnableExtensions

set "SCRIPT_DIR=%~dp0"
set "PROJECT=%SCRIPT_DIR%src\PkzTool.App.Wpf\PkzTool.App.Wpf.csproj"
set "OUTPUT=%SCRIPT_DIR%src\PkzTool.App.Wpf\bin\Release\net8.0-windows\publish\win-x64"
set "EXENAME=PKZ Explorer.exe"

set "NOPAUSE=0"
set "MAKE_ARCHIVES=1"

:parse
if "%~1"=="" goto :parsed
if /I "%~1"=="/nopause"    set "NOPAUSE=1"
if /I "%~1"=="/noarchives" set "MAKE_ARCHIVES=0"
if /I "%~1"=="-nopause"    set "NOPAUSE=1"
if /I "%~1"=="-noarchives" set "MAKE_ARCHIVES=0"
shift
goto :parse
:parsed

where dotnet >nul 2>nul
if errorlevel 1 (
    echo [error] "dotnet" was not found on PATH. Install the .NET 8 SDK and try again.
    goto :fail
)

if not exist "%PROJECT%" (
    echo [error] Project not found: "%PROJECT%"
    goto :fail
)

rem Always rebuild the output folder. An incremental publish can leave a partial result behind and
rem the smoke test below would then run against a folder that does not match this source tree.
echo Cleaning "%OUTPUT%" ...
if exist "%OUTPUT%" rmdir /s /q "%OUTPUT%"

rem ---- publish settings -------------------------------------------------------
set "ARGS=-c Release -f net8.0-windows -r win-x64"
set "ARGS=%ARGS% --self-contained false"
set "ARGS=%ARGS% -p:PublishSingleFile=true"
set "ARGS=%ARGS% -p:PublishReadyToRun=false"
set "ARGS=%ARGS% -p:PublishTrimmed=false"
set "ARGS=%ARGS% -p:SatelliteResourceLanguages=en"

echo ============================================================================
echo  Publishing PKZ Explorer (Release, single file, framework-dependent, win-x64)
echo ============================================================================
echo  Project : %PROJECT%
echo  Output  : %OUTPUT%
echo  Command : dotnet publish %ARGS% -o "%OUTPUT%"
echo.

dotnet publish "%PROJECT%" %ARGS% -o "%OUTPUT%"
if errorlevel 1 (
    echo.
    echo [error] Publish failed.
    goto :fail
)

set "EXE=%OUTPUT%\%EXENAME%"
if not exist "%EXE%" (
    echo.
    echo [error] Publish reported success but "%EXE%" is missing.
    goto :fail
)

rem dotnet publish never deletes a file it no longer produces, so a renamed build would otherwise
rem leave the previous executable beside the new one. Keep only the expected exe.
for %%F in ("%OUTPUT%\*.exe") do if /I not "%%~nxF"=="%EXENAME%" del /q "%%F"
del /q "%OUTPUT%\*.pdb" 2>nul

echo.
echo ----------------------------------------------------------------------------
echo  Published files:
for /f "delims=" %%F in ('dir /b /a-d "%OUTPUT%" 2^>nul') do echo    %%F
echo ----------------------------------------------------------------------------
echo  Executable: "%EXE%"
echo.

rem A smoke test that the published build actually starts and its XAML is intact. --uitest builds
rem the shell and every dialog before any window is shown, so it exercises the resources a
rem single-file publish is most likely to lose. It needs no archive and no Oodle dll.
echo Running published UI smoke test...
"%EXE%" --uitest
if errorlevel 1 (
    echo.
    echo [error] The published executable reported a UI smoke-test failure.
    goto :fail
)

rem And that the command line still works, since the CLI lives in the same executable.
echo.
echo Running published CLI check...
"%EXE%" --help >nul
if errorlevel 1 (
    echo.
    echo [error] The published executable reported a CLI failure.
    goto :fail
)

rem The tool keeps its settings beside the executable, so running it wrote settings.json into the
rem output. That file is this machine's state (theme, columns, recent files), not something to ship,
rem so it is removed after the checks that produced it.
if exist "%OUTPUT%\settings.json" (
    echo Removing the smoke test's settings.json from the output ...
    del /q "%OUTPUT%\settings.json"
)

if "%MAKE_ARCHIVES%"=="1" goto :archives
goto :done

rem ---- source archive --------------------------------------------------------
rem git archive the committed tree (tracked files at HEAD) into src.zip, replacing any existing
rem archive so the zip always matches HEAD. Uncommitted work is deliberately absent: the archive is
rem meant to describe a build that exists in the history.
:archives
set "SRC_ZIP=%SCRIPT_DIR%src.zip"

where git >nul 2>nul
if errorlevel 1 (
    echo.
    echo [error] "git" was not found on PATH, so src.zip cannot be created.
    echo         Re-run with /noarchives to publish without the archives.
    goto :fail
)

rem The repository check runs first, because "not a git repository" from git archive is easy to
rem misread as a missing HEAD rather than a missing checkout.
git -C "%SCRIPT_DIR%." rev-parse --is-inside-work-tree >nul 2>nul
if errorlevel 1 (
    echo.
    echo [error] "%SCRIPT_DIR%." is not a git repository, so there is no HEAD to archive.
    echo         Re-run with /noarchives to publish without the archives.
    goto :fail
)

rem A dirty tree means src.zip will not match the binary just built. That is worth saying rather than
rem discovering later, but it is not a failure: a maintainer may be publishing a test build.
git -C "%SCRIPT_DIR%." diff --quiet 2>nul
if errorlevel 1 echo [warn] The working tree has uncommitted changes; src.zip archives HEAD only.

echo.
echo Creating "%SRC_ZIP%" from HEAD ...
if exist "%SRC_ZIP%" del /f /q "%SRC_ZIP%"
git -C "%SCRIPT_DIR%." archive --format=zip -o "%SRC_ZIP%" HEAD
if errorlevel 1 (
    echo [error] git archive failed.
    goto :fail
)
echo    %SRC_ZIP%

rem ---- binary archive --------------------------------------------------------
set "ZIP=%SCRIPT_DIR%PkzExplorer-win-x64.zip"
echo.
echo Creating "%ZIP%" from "%OUTPUT%" ...
if exist "%ZIP%" del /f /q "%ZIP%"
rem Compress-Archive is used because PowerShell ships with Windows, so this needs nothing installed
rem beyond the .NET SDK the publish already required. The zip is rebuilt from scratch each time.
powershell -NoProfile -NonInteractive -Command ^
  "Compress-Archive -Path '%OUTPUT%\*' -DestinationPath '%ZIP%' -Force"
if errorlevel 1 (
    echo [error] Creating the zip failed.
    goto :fail
)
echo    %ZIP%

echo.
echo  Artifacts beside this script:
echo    %SRC_ZIP%
echo    %ZIP%

:done
echo.
echo [ok] Done.
goto :end

:fail
if "%NOPAUSE%"=="0" pause
endlocal
exit /b 1

:end
if "%NOPAUSE%"=="0" pause
endlocal
exit /b 0
