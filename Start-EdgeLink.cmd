@echo off
rem Double-click to start the EdgeLink companion (Windows PowerShell 5.1).
"%WINDIR%\System32\WindowsPowerShell\v1.0\powershell.exe" -NoProfile -ExecutionPolicy Bypass -File "%~dp0companion\Start-EdgeLink.ps1" %*
