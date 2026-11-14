@echo off
rem Builds EdgeLink.exe with the C# compiler that ships with Windows (.NET Framework 4.x).
rem Output: dist\EdgeLink\  (EdgeLink.exe + widget\ + addon\ + EdgeLink.ini)
setlocal
cd /d "%~dp0"
set CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe
if not exist "%CSC%" set CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe
if not exist "%CSC%" ( echo csc.exe not found - .NET Framework 4.x is required. & exit /b 1 )
set OUT=..\dist\EdgeLink
if not exist "%OUT%" mkdir "%OUT%"
"%CSC%" /nologo /target:winexe /optimize+ /platform:anycpu /out:"%OUT%\EdgeLink.exe" /win32icon:EdgeLink.ico ^
  /r:System.dll /r:System.Core.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll ^
  AssemblyInfo.cs EdgeLinkApp.cs EdgeLinkCore.cs
if errorlevel 1 ( echo BUILD FAILED & exit /b 1 )
robocopy ..\widget "%OUT%\widget" /MIR /NFL /NDL /NJH /NJS >nul
robocopy ..\addon "%OUT%\addon" /MIR /NFL /NDL /NJH /NJS >nul
copy /y EdgeLink.exe.config "%OUT%\EdgeLink.exe.config" >nul
if not exist "%OUT%\EdgeLink.ini" copy /y EdgeLink.ini "%OUT%\EdgeLink.ini" >nul
rem Package the iCUE widget (needs Node.js). Output: edgelink-wow.icuewidget next to EdgeLink.exe
where npx.cmd >nul 2>&1
if errorlevel 1 ( echo Node.js not found - skipped .icuewidget packaging. & goto done )
pushd "%OUT%"
del /q *.icuewidget 2>nul
call npx.cmd -y icuewidget-cli package widget
popd
:done
echo Built %OUT%\EdgeLink.exe
exit /b 0
