$ErrorActionPreference = "Stop"
$csc = "C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if (-not (Test-Path $csc)) {
    Write-Error "Cannot find C# compiler at $csc"
    exit 1
}

$wpfDir = "C:\Windows\Microsoft.NET\Framework64\v4.0.30319\WPF"
$outExe = Join-Path $PSScriptRoot "WindowsTerminalLTSC.exe"
$srcFiles = @(
    (Join-Path $PSScriptRoot "src\Program.cs"),
    (Join-Path $PSScriptRoot "src\AnsiParser.cs"),
    (Join-Path $PSScriptRoot "src\TerminalSession.cs"),
    (Join-Path $PSScriptRoot "src\TerminalTheme.cs"),
    (Join-Path $PSScriptRoot "src\SystemIntegration.cs"),
    (Join-Path $PSScriptRoot "src\MainWindow.cs")
)

Write-Host "Compiling WindowsTerminalLTSC.exe for Windows 10 LTSC 2019..." -ForegroundColor Cyan
& $csc /nologo /codepage:65001 /target:winexe /out:$outExe /optimize+ `
    /r:"$wpfDir\PresentationFramework.dll" `
    /r:"$wpfDir\PresentationCore.dll" `
    /r:"$wpfDir\WindowsBase.dll" `
    /r:"System.Xaml.dll" `
    /r:"System.Drawing.dll" `
    /r:"System.Windows.Forms.dll" `
    /r:"System.Web.Extensions.dll" `
    $srcFiles

if ($LASTEXITCODE -eq 0) {
    Write-Host "Build succeeded: $outExe" -ForegroundColor Green
} else {
    Write-Error "Build failed with exit code $LASTEXITCODE"
    exit $LASTEXITCODE
}
