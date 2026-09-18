@echo off
rem ===========================================================================
rem  HashTool build script (ASCII only, so any code page works)
rem  Uses the C# compiler that ships with Windows (.NET Framework 4.x).
rem  No installs, no SDK, no third-party tools required.
rem
rem  Double-click this file, or run:  build.bat
rem  Output: dist\HashTool.exe   (single desktop exe, GUI only)
rem ===========================================================================
setlocal

set "ROOT=%~dp0"
set "CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if not exist "%CSC%" set "CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe"
if not exist "%CSC%" (
    echo [ERROR] csc.exe not found. .NET Framework 4.x should be part of Windows.
    exit /b 1
)

if not exist "%ROOT%dist" mkdir "%ROOT%dist"

set "OPTS=/nologo /optimize+ /platform:anycpu /codepage:65001 /warnaserror+:649"
set "REFS=/reference:System.dll /reference:System.Drawing.dll /reference:System.Windows.Forms.dll /reference:System.Core.dll"
if exist "%ROOT%src\app.ico" set "ICON=/win32icon:%ROOT%src\app.ico"
if exist "%ROOT%src\app.ico" echo Icon     : %ROOT%src\app.ico

echo Compiler : %CSC%
echo Source   : %ROOT%src\HashTool.cs
echo.

echo building dist\HashTool.exe ...
"%CSC%" %OPTS% %REFS% %ICON% /win32manifest:"%ROOT%src\app.manifest" /target:winexe /out:"%ROOT%dist\HashTool.exe" "%ROOT%src\HashTool.cs"
if errorlevel 1 goto fail

echo.
echo Done:
for %%F in ("%ROOT%dist\HashTool.exe") do echo   %%~fF  (%%~zF bytes)
echo.
echo Verify with:  dist\HashTool.exe --selftest
exit /b 0

:fail
echo.
echo [ERROR] build failed.
exit /b 1
