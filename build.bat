@echo off
chcp 65001 >nul
set "CSC=C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
set "WPF=C:\Windows\Microsoft.NET\Framework64\v4.0.30319\WPF"

echo [1/2] Compiling WindowsTerminalLTSC.exe ...
set "ICON_ARG="
if exist "%~dp0app.ico" set "ICON_ARG=/win32icon:%~dp0app.ico"
"%CSC%" /nologo /codepage:65001 /target:winexe %ICON_ARG% /out:"%~dp0WindowsTerminalLTSC.exe" /optimize+ ^
    /r:"%WPF%\PresentationFramework.dll" ^
    /r:"%WPF%\PresentationCore.dll" ^
    /r:"%WPF%\WindowsBase.dll" ^
    /r:"System.Xaml.dll" ^
    /r:"System.Drawing.dll" ^
    /r:"System.Windows.Forms.dll" ^
    /r:"System.Web.Extensions.dll" ^
    "%~dp0src\AssemblyInfo.cs" "%~dp0src\Program.cs" "%~dp0src\AnsiParser.cs" "%~dp0src\TerminalSession.cs" "%~dp0src\TerminalTheme.cs" "%~dp0src\SystemIntegration.cs" "%~dp0src\MainWindow.cs"

if %ERRORLEVEL% NEQ 0 (
    echo Build WindowsTerminalLTSC.exe failed!
    pause
    exit /b 1
)

echo [2/2] Compiling Self-Contained Installer WindowsTerminalLTSC-Setup.exe ...
"%CSC%" /nologo /codepage:65001 /target:winexe %ICON_ARG% /out:"%~dp0WindowsTerminalLTSC-Setup.exe" /optimize+ ^
    /r:"%WPF%\PresentationFramework.dll" ^
    /r:"%WPF%\PresentationCore.dll" ^
    /r:"%WPF%\WindowsBase.dll" ^
    /r:"System.Xaml.dll" ^
    /r:"System.Drawing.dll" ^
    /r:"System.Windows.Forms.dll" ^
    /resource:"%~dp0WindowsTerminalLTSC.exe,WindowsTerminalLTSC.exe" ^
    /resource:"%~dp0app.ico,app.ico" ^
    /resource:"%~dp0app.png,app.png" ^
    "%~dp0src\AssemblyInfo.cs" "%~dp0installer\SetupProgram.cs"

if %ERRORLEVEL% EQU 0 (
    echo Build succeeded:
    echo   - %~dp0WindowsTerminalLTSC.exe
    echo   - %~dp0WindowsTerminalLTSC-Setup.exe
) else (
    echo Build Setup failed!
    pause
)
