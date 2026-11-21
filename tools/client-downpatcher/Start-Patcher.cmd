@echo off
setlocal DisableDelayedExpansion
title Eden Client Downpatcher
"%~dp0runtime\python.exe" -B "%~dp0Prompt-Patcher.py"
set "patch_exit=%errorlevel%"
echo(
pause
exit /b %patch_exit%
