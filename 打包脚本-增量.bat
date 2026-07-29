@echo off
setlocal

chcp 65001 >nul
cd /d "%~dp0"

set "VERSION_ARG="
if not "%~1"=="" set "VERSION_ARG=-Version %~1"

echo [WARN] Review RELEASE_CHECKLIST.md before publishing.
echo [WARN] Unconfirmed real-device checks no longer block packaging.

pwsh -NoProfile -ExecutionPolicy Bypass -File "Tools\Publish-CleanPackage.ps1" %VERSION_ARG%

set "EXIT_CODE=%ERRORLEVEL%"
echo.
if "%EXIT_CODE%"=="0" (
    echo Package completed.
) else (
    echo Package failed. Exit code: %EXIT_CODE%
)
pause
exit /b %EXIT_CODE%
