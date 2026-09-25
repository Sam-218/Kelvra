<#
.SYNOPSIS
    Signs Kelvra.exe (or any .exe/.dll) with a code-signing certificate that shows your name.

.DESCRIPTION
    Without -Thumbprint or -PfxPath, the script creates (once) a SELF-SIGNED certificate for
    "CN=<Publisher>" in your personal certificate store and signs with it.

    IMPORTANT: a self-signed certificate is only trusted on PCs where it has been installed.
    Your name will be visible under Properties > Digital Signatures, but on other people's PCs
    Windows reports it as "not trusted" and SmartScreen / antivirus warnings do NOT go away.
    Only a certificate from a real certificate authority (Certum, Sectigo, SignPath, ...) does that.
    When you have one, run this same script with -Thumbprint or -PfxPath.

.EXAMPLE
    .\sign.ps1 -Publisher "Sam Example"
        Creates/reuses a self-signed certificate for "Sam Example" and signs publish\Kelvra.exe.

.EXAMPLE
    .\sign.ps1 -Publisher "Sam Example" -TrustOnThisPC
        Same, and also trusts the certificate on THIS PC only, so the signature shows as valid here.

.EXAMPLE
    .\sign.ps1 -Thumbprint 0123ABCD...   (a real certificate installed in your store, e.g. from Certum)
    .\sign.ps1 -PfxPath my-cert.pfx      (a real certificate in a .pfx file - you'll be asked for its password)
#>
[CmdletBinding()]
param(
    [string]$File = (Join-Path $PSScriptRoot "..\publish\Kelvra.exe"),
    [string]$Publisher,
    [string]$Thumbprint,
    [string]$PfxPath,
    [switch]$TrustOnThisPC,
    [string]$TimestampServer = "http://timestamp.digicert.com",
    [switch]$NoTimestamp
)

$ErrorActionPreference = "Stop"

function Write-Step($text) { Write-Host "> $text" -ForegroundColor Cyan }

# ---------- the file ----------
$File = [IO.Path]::GetFullPath($File)
if (-not (Test-Path -LiteralPath $File)) { throw "File not found: $File (run 'dotnet publish' first)" }

# ---------- pick a certificate ----------
$cert = $null
$selfSigned = $false

if ($PfxPath) {
    Write-Step "Using certificate file $PfxPath"
    $password = Read-Host "Password for $PfxPath" -AsSecureString
    $cert = Get-PfxCertificate -FilePath $PfxPath -Password $password
}
elseif ($Thumbprint) {
    Write-Step "Using installed certificate $Thumbprint"
    $cert = Get-ChildItem Cert:\CurrentUser\My, Cert:\LocalMachine\My -CodeSigningCert |
            Where-Object Thumbprint -eq $Thumbprint.Replace(' ', '').ToUpper() | Select-Object -First 1
    if (-not $cert) { throw "No code-signing certificate with thumbprint $Thumbprint found." }
}
else {
    $selfSigned = $true
    if (-not $Publisher) { $Publisher = Read-Host "Name to show as publisher (e.g. your full name)" }
    $Publisher = $Publisher.Trim()
    if (-not $Publisher) { throw "A publisher name is required." }
    if ($Publisher -match '[,=+<>#;"\\]') { throw "Please use a name without , = + < > # ; `" \ characters." }

    $subject = "CN=$Publisher"
    $cert = Get-ChildItem Cert:\CurrentUser\My -CodeSigningCert |
            Where-Object { $_.Subject -eq $subject -and $_.NotAfter -gt (Get-Date).AddDays(7) } |
            Sort-Object NotAfter -Descending | Select-Object -First 1

    if ($cert) {
        Write-Step "Reusing your self-signed certificate for '$Publisher' (valid until $($cert.NotAfter.ToString('d')))"
    }
    else {
        Write-Step "Creating a self-signed code-signing certificate for '$Publisher'"
        $cert = New-SelfSignedCertificate -Type CodeSigningCert -Subject $subject `
                    -CertStoreLocation Cert:\CurrentUser\My -KeyAlgorithm RSA -KeyLength 3072 `
                    -HashAlgorithm SHA256 -NotAfter (Get-Date).AddYears(5) `
                    -FriendlyName "Code signing - $Publisher"
    }
}

# ---------- optionally trust it on this PC ----------
if ($TrustOnThisPC) {
    if (-not $selfSigned) {
        Write-Host "  -TrustOnThisPC is only needed for self-signed certificates; skipping." -ForegroundColor DarkGray
    }
    else {
        $cer = Join-Path $env:TEMP "codesign-$($cert.Thumbprint).cer"
        Export-Certificate -Cert $cert -FilePath $cer | Out-Null
        foreach ($store in 'Root', 'TrustedPublisher') {
            $already = Get-ChildItem "Cert:\CurrentUser\$store" | Where-Object Thumbprint -eq $cert.Thumbprint
            if (-not $already) {
                Write-Step "Trusting the certificate on this PC ($store). Windows may ask you to confirm."
                Import-Certificate -FilePath $cer -CertStoreLocation "Cert:\CurrentUser\$store" | Out-Null
            }
        }
        Remove-Item -LiteralPath $cer
    }
}

# ---------- sign ----------
Write-Step "Signing $File"
$params = @{ FilePath = $File; Certificate = $cert; HashAlgorithm = 'SHA256' }
if (-not $NoTimestamp) { $params.TimestampServer = $TimestampServer }
try {
    $result = Set-AuthenticodeSignature @params
}
catch {
    if ($NoTimestamp) { throw }
    Write-Host "  Timestamp server didn't respond - signing without a timestamp." -ForegroundColor Yellow
    $params.Remove('TimestampServer')
    $result = Set-AuthenticodeSignature @params
}

# ---------- report ----------
$check = Get-AuthenticodeSignature -FilePath $File
$name = $check.SignerCertificate.GetNameInfo('SimpleName', $false)
Write-Host ""
Write-Host "Signed by : $name" -ForegroundColor Green
Write-Host "Status    : $($check.Status)  $($check.StatusMessage)"
if ($check.TimeStamperCertificate) { Write-Host "Timestamp : $($check.TimeStamperCertificate.GetNameInfo('SimpleName', $false))" }

if ($selfSigned) {
    Write-Host ""
    Write-Host "Note: this is a self-signed certificate." -ForegroundColor Yellow
    Write-Host "  Your name is in the file (Properties > Digital Signatures), but other PCs will show it as"
    Write-Host "  not trusted and SmartScreen/antivirus warnings stay. For that you need a certificate from a"
    Write-Host "  certificate authority (e.g. Certum) or SignPath - then run: .\sign.ps1 -Thumbprint <thumbprint>"
}
