@echo off
setlocal

if not "%~1"=="" set "MTIMER_NAS_HOST=%~1"
if not "%~2"=="" set "MTIMER_NAS_USER=%~2"
if not "%~3"=="" set "MTIMER_NAS_REMOTE_PATH=%~3"
if not "%~4"=="" set "MTIMER_NAS_REVISION=%~4"

if not defined MTIMER_NAS_HOST set /p "MTIMER_NAS_HOST=NAS host or IP: "
if not defined MTIMER_NAS_USER set /p "MTIMER_NAS_USER=NAS SSH user: "
if not defined MTIMER_NAS_REMOTE_PATH set /p "MTIMER_NAS_REMOTE_PATH=Remote MTimer path: "
if not defined MTIMER_NAS_REVISION set "MTIMER_NAS_REVISION=HEAD"

if /I "%MTIMER_NAS_VALIDATE_ONLY%"=="1" (
  powershell.exe -NoProfile -ExecutionPolicy Bypass ^
    -File "%~dp0Deploy-MTimerSyncNas.ps1" ^
    -NasHost "%MTIMER_NAS_HOST%" ^
    -NasUser "%MTIMER_NAS_USER%" ^
    -RemoteProjectPath "%MTIMER_NAS_REMOTE_PATH%" ^
    -Revision "%MTIMER_NAS_REVISION%" ^
    -ValidateOnly
) else (
  powershell.exe -NoProfile -ExecutionPolicy Bypass ^
    -File "%~dp0Deploy-MTimerSyncNas.ps1" ^
    -NasHost "%MTIMER_NAS_HOST%" ^
    -NasUser "%MTIMER_NAS_USER%" ^
    -RemoteProjectPath "%MTIMER_NAS_REMOTE_PATH%" ^
    -Revision "%MTIMER_NAS_REVISION%"
)

set "DEPLOY_EXIT_CODE=%ERRORLEVEL%"
if /I not "%MTIMER_DEPLOY_NO_PAUSE%"=="1" pause
exit /b %DEPLOY_EXIT_CODE%
