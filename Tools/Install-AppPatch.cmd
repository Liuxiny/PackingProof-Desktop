@echo off
setlocal

chcp 65001 >nul
title 快递打包监控 - 准备增量更新
cd /d "%~dp0"

echo 快递打包监控手动增量更新
echo.
echo 脚本会校验增量补丁，并将其放入启动器更新缓存。
echo 不会修改正在运行的程序，也不需要选择软件安装目录。
echo 准备完成后，请关闭并重新打开快递打包监控以完成安装。
echo 请勿单独移动此 CMD、stage_app_patch.ps1、update_manifest.json 或 AppPatch ZIP。
echo.

where powershell.exe >nul 2>nul
if errorlevel 1 (
    echo [错误] 未找到 Windows PowerShell，无法安装增量更新。
    echo.
    pause
    exit /b 1
)

set "EPM_APP_PATCH_ROOT=%~dp0"
powershell.exe -NoProfile -ExecutionPolicy Bypass -Command "$root=$env:EPM_APP_PATCH_ROOT; $scriptPath=Join-Path $root 'stage_app_patch.ps1'; $scriptText=[System.IO.File]::ReadAllText($scriptPath,[System.Text.Encoding]::UTF8); & ([ScriptBlock]::Create($scriptText)) -PackageRoot $root"
set "EXIT_CODE=%ERRORLEVEL%"

echo.
if "%EXIT_CODE%"=="0" (
    echo 更新准备流程已完成。
) else (
    echo 更新未准备完成，请根据上方提示处理后重试。
)
echo.
pause
exit /b %EXIT_CODE%
