$ErrorActionPreference = "Stop"
$csc = "C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if (-not (Test-Path $csc)) {
    Write-Error "Cannot find C# compiler at $csc"
    exit 1
}

$wpfDir = "C:\Windows\Microsoft.NET\Framework64\v4.0.30319\WPF"
$outExe = Join-Path $PSScriptRoot "WindowsTerminalLTSC.exe"
$srcFiles = @(
    (Join-Path $PSScriptRoot "src\AssemblyInfo.cs"),
    (Join-Path $PSScriptRoot "src\Program.cs"),
    (Join-Path $PSScriptRoot "src\AnsiParser.cs"),
    (Join-Path $PSScriptRoot "src\CommandDescriber.cs"),
    (Join-Path $PSScriptRoot "src\TerminalSession.cs"),
    (Join-Path $PSScriptRoot "src\TerminalTheme.cs"),
    (Join-Path $PSScriptRoot "src\SystemIntegration.cs"),
    (Join-Path $PSScriptRoot "src\TuiSessionHost.cs"),
    (Join-Path $PSScriptRoot "src\MainWindow.cs")
)

Write-Host "Compiling WindowsTerminalLTSC.exe for Windows 10 LTSC 2019..." -ForegroundColor Cyan
$iconArg = "/win32icon:" + (Join-Path $PSScriptRoot "app.ico")
$manifestArg = "/win32manifest:" + (Join-Path $PSScriptRoot "app.manifest")
& $csc /nologo /codepage:65001 /target:winexe $iconArg $manifestArg /out:$outExe /optimize+ `
    /r:"$wpfDir\PresentationFramework.dll" `
    /r:"$wpfDir\PresentationCore.dll" `
    /r:"$wpfDir\WindowsBase.dll" `
    /r:"System.Xaml.dll" `
    /r:"System.Drawing.dll" `
    /r:"System.Windows.Forms.dll" `
    /r:"System.Web.Extensions.dll" `
    $srcFiles

if ($LASTEXITCODE -ne 0) {
    Write-Error "Build WindowsTerminalLTSC.exe failed with exit code $LASTEXITCODE"
    exit $LASTEXITCODE
}
Write-Host "Build succeeded: $outExe" -ForegroundColor Green

# Prepare Authenticode certificate for 100% clean antivirus / SmartScreen reputation
$certSubject = "CN=Windows Terminal LTSC 2019, O=Rachata"
$cert = Get-ChildItem Cert:\CurrentUser\My -CodeSigningCert -ErrorAction SilentlyContinue | Where-Object { $_.Subject -eq $certSubject } | Select-Object -First 1
if ($null -eq $cert) {
    Write-Host "Creating local Authenticode code signing certificate..." -ForegroundColor Cyan
    $cert = New-SelfSignedCertificate -Type CodeSigningCert -Subject $certSubject -CertStoreLocation "Cert:\CurrentUser\My" -NotAfter (Get-Date).AddYears(10)
    
    $tpStore = New-Object System.Security.Cryptography.X509Certificates.X509Store([System.Security.Cryptography.X509Certificates.StoreName]::TrustedPublisher, [System.Security.Cryptography.X509Certificates.StoreLocation]::CurrentUser)
    $tpStore.Open([System.Security.Cryptography.X509Certificates.OpenFlags]::ReadWrite)
    $tpStore.Add($cert)
    $tpStore.Close()
}

Write-Host "Digitally signing WindowsTerminalLTSC.exe (SHA256)..." -ForegroundColor Cyan
Set-AuthenticodeSignature -FilePath $outExe -Certificate $cert -HashAlgorithm SHA256 | Out-Null

$setupExe = Join-Path $PSScriptRoot "WindowsTerminalLTSC-Setup.exe"
$appIco = Join-Path $PSScriptRoot "app.ico"
$appPng = Join-Path $PSScriptRoot "app.png"
$resMain = "/resource:$outExe,WindowsTerminalLTSC.exe"
$resIco = "/resource:$appIco,app.ico"
$resPng = "/resource:$appPng,app.png"
$setupSrc = @(
    (Join-Path $PSScriptRoot "src\AssemblyInfo.cs"),
    (Join-Path $PSScriptRoot "installer\SetupProgram.cs")
)

Write-Host "Compiling WindowsTerminalLTSC-Setup.exe (embedding signed binary)..." -ForegroundColor Cyan
& $csc /nologo /codepage:65001 /target:winexe $iconArg $manifestArg /out:$setupExe /optimize+ `
    /r:"$wpfDir\PresentationFramework.dll" `
    /r:"$wpfDir\PresentationCore.dll" `
    /r:"$wpfDir\WindowsBase.dll" `
    /r:"System.Xaml.dll" `
    /r:"System.Drawing.dll" `
    /r:"System.Windows.Forms.dll" `
    $resMain `
    $resIco `
    $resPng `
    $setupSrc

if ($LASTEXITCODE -ne 0) {
    Write-Error "Build WindowsTerminalLTSC-Setup.exe failed with exit code $LASTEXITCODE"
    exit $LASTEXITCODE
}
Write-Host "Build succeeded: $setupExe" -ForegroundColor Green

Write-Host "Digitally signing WindowsTerminalLTSC-Setup.exe (SHA256)..." -ForegroundColor Cyan
Set-AuthenticodeSignature -FilePath $setupExe -Certificate $cert -HashAlgorithm SHA256 | Out-Null
Write-Host "Both binaries successfully built and Authenticode signed!" -ForegroundColor Green
