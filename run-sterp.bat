@echo off
rem ============================================================================
rem  STERP - Supply Chain Management (modernized) - demo launcher
rem
rem  Starts the four STERP services on the isolated demo database, and stops
rem  them again. Double-click it to start everything.
rem     STERP.Web         GUI           https://localhost:7140
rem     STERP.ApiPortal   API Console   https://localhost:7143  (browse and try the APIs)
rem     STERP.Api         online API    port 7141 (web service, no pages)
rem     STERP.Jobs        batch jobs    port 7142 (web service, no pages)
rem
rem  Usage:
rem     run-sterp.bat              build, start everything, wait until ready, open the GUI
rem     run-sterp.bat /nobuild     start the existing binaries without rebuilding
rem     run-sterp.bat /stop        stop the services
rem     run-sterp.bat /restart     stop, then start again
rem     run-sterp.bat /reset       stop, restore the demo data, then start again
rem     run-sterp.bat /status      report whether each service is up and ready
rem     run-sterp.bat /?           show this help
rem
rem  Options:
rem     /nobrowser   do not open the GUI in the browser
rem     /force       stop even while STERP.Jobs is executing batch runs
rem
rem  The services run in the background without windows. The first start
rem  creates the demo database: LocalDB instance STERPA08_demo with the demo
rem  dataset and your Windows login mapped to SYSADM. The work is done by
rem  scripts\run-sterp.ps1, which also takes -Name and -PortBase for a second
rem  demo environment.
rem
rem  Requires: .NET 10 SDK, PowerShell 7, SQL Server LocalDB and the dataset
rem  backups (README.md, Prerequisites). No shared database is ever used.
rem ============================================================================

setlocal EnableExtensions

rem --- Capture this script's folder first: SHIFT below also shifts %0.
set "STERP_ROOT=%~dp0"

set "ACTION=Start"
set "OPTIONS="
set "BAD_OPTION="

:parse_args
if "%~1"=="" goto args_done
set "ARG=%~1"
if "%ARG:~0,2%"=="--" set "ARG=/%ARG:~2%"
if "%ARG:~0,1%"=="-" set "ARG=/%ARG:~1%"
if /I "%ARG%"=="/start"     (set "ACTION=Start"   & goto next_arg)
if /I "%ARG%"=="/stop"      (set "ACTION=Stop"    & goto next_arg)
if /I "%ARG%"=="/restart"   (set "ACTION=Restart" & goto next_arg)
if /I "%ARG%"=="/reset"     (set "ACTION=Reset"   & goto next_arg)
if /I "%ARG%"=="/status"    (set "ACTION=Status"  & goto next_arg)
if /I "%ARG%"=="/nobuild"   (set "OPTIONS=%OPTIONS% -NoBuild"   & goto next_arg)
if /I "%ARG%"=="/nobrowser" (set "OPTIONS=%OPTIONS% -NoBrowser" & goto next_arg)
if /I "%ARG%"=="/force"     (set "OPTIONS=%OPTIONS% -Force"     & goto next_arg)
if "%ARG%"=="/?"            (set "ACTION=Help"    & goto next_arg)
if /I "%ARG%"=="/h"         (set "ACTION=Help"    & goto next_arg)
if /I "%ARG%"=="/help"      (set "ACTION=Help"    & goto next_arg)
echo.
echo  Unknown option "%~1".
set "ACTION=Help"
set "BAD_OPTION=1"
goto args_done

:next_arg
shift
goto parse_args

:args_done
where pwsh >nul 2>&1
if errorlevel 1 (
    echo.
    echo  PowerShell 7 ^(pwsh^) is required: winget install Microsoft.PowerShell
    echo.
    pause
    exit /b 1
)

pwsh -NoProfile -ExecutionPolicy Bypass -File "%STERP_ROOT%scripts\run-sterp.ps1" -Action %ACTION% %OPTIONS%
if defined BAD_OPTION exit /b 1
exit /b %ERRORLEVEL%
