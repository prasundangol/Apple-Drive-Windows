<#
.SYNOPSIS
    Builds a signed MSIX package of Apple Drive.

.DESCRIPTION
    1. Finds, or creates, a code-signing certificate for "CN=Prasun Dangol" in the current user's
       certificate store (no administrator rights needed), and exports its public part as a .cer file.
    2. Publishes the app as a self-contained, signed MSIX for the chosen architecture.

    The package is written to artifacts\package. To install it on a PC, that PC must trust the
    certificate once; see "Installing the MSIX package" in the README.

.PARAMETER Platform
    x64 (default) or ARM64.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File tools/Build-Package.ps1
#>
param(
    [ValidateSet('x64', 'ARM64')]
    [string]$Platform = 'x64'
)

$ErrorActionPreference = 'Stop'
$root = Resolve-Path (Join-Path $PSScriptRoot '..')
$output = Join-Path $root 'artifacts\package'
New-Item -ItemType Directory -Force $output | Out-Null

# The certificate subject must match the manifest's Publisher.
$subject = 'CN=Prasun Dangol'
$certificate = Get-ChildItem Cert:\CurrentUser\My |
    Where-Object { $_.Subject -eq $subject -and $_.HasPrivateKey -and $_.NotAfter -gt (Get-Date).AddDays(30) -and
                   ($_.EnhancedKeyUsageList.ObjectId -contains '1.3.6.1.5.5.7.3.3') } |
    Sort-Object NotAfter -Descending | Select-Object -First 1
if (-not $certificate) {
    Write-Host "Creating a self-signed code-signing certificate for $subject"
    $certificate = New-SelfSignedCertificate -Type Custom -Subject $subject -KeyUsage DigitalSignature `
        -FriendlyName 'Apple Drive package signing' -CertStoreLocation Cert:\CurrentUser\My `
        -TextExtension @('2.5.29.37={text}1.3.6.1.5.5.7.3.3', '2.5.29.19={text}') -NotAfter (Get-Date).AddYears(3)
}

$cer = Join-Path $output 'AppleDrive.cer'
Export-Certificate -Cert $certificate -FilePath $cer | Out-Null
Write-Host "Signing with $($certificate.Thumbprint) (public certificate: $cer)"

$project = Join-Path $root 'src\AppleDrive.App\AppleDrive.App.csproj'
& dotnet publish $project -c Release `
    -p:Platform=$Platform `
    -p:WindowsPackageType=MSIX `
    -p:GenerateAppxPackageOnBuild=true `
    -p:AppxBundle=Never `
    -p:AppxPackageSigningEnabled=true `
    -p:PackageCertificateThumbprint=$($certificate.Thumbprint) `
    -p:AppxPackageDir="$output/"
if ($LASTEXITCODE -ne 0) { throw "Packaging failed ($LASTEXITCODE)" }

$package = Get-ChildItem $output -Recurse -Filter *.msix | Sort-Object LastWriteTime -Descending | Select-Object -First 1
Write-Host "Package: $($package.FullName)"
