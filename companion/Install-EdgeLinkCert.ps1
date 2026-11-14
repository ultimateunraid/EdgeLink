<#
.SYNOPSIS
    Creates a localhost HTTPS certificate for the EdgeLink companion (current user only, no admin).

.DESCRIPTION
    iCUE's Web URL widget loads http://localhost:8787 as https. This script:
      1. Creates a small local CA ("EdgeLink Local CA") and a localhost certificate signed by it.
      2. Adds the CA's public certificate to YOUR Trusted Root store. Windows shows a
         security prompt; click Yes.
      3. Deletes the CA's private key, so nothing else can ever be signed with it.
    The localhost certificate (with its key) stays in Cert:\CurrentUser\My, where
    Start-EdgeLink.ps1 picks it up automatically. It is valid for localhost, 127.0.0.1 and ::1 only.

.PARAMETER Remove
    Remove all EdgeLink certificates (both stores) and exit.
#>
[CmdletBinding()]
param([switch]$Remove)

$ErrorActionPreference = 'Stop'
$leafName = 'EdgeLink localhost'
$caName = 'EdgeLink Local CA'

function Remove-EdgeLinkCerts {
    foreach ($store in 'Cert:\CurrentUser\My', 'Cert:\CurrentUser\Root') {
        Get-ChildItem $store | Where-Object { $_.Subject -eq "CN=$caName" -or $_.FriendlyName -eq $leafName } |
            ForEach-Object { Write-Host "Removing $($_.Subject) [$($_.Thumbprint)] from $store"; Remove-Item $_.PSPath }
    }
}

Remove-EdgeLinkCerts
if ($Remove) { Write-Host 'EdgeLink certificates removed.'; return }

$ca = New-SelfSignedCertificate -Subject "CN=$caName" -FriendlyName $caName `
    -KeyUsage CertSign, CRLSign, DigitalSignature `
    -TextExtension @('2.5.29.19={critical}{text}ca=1&pathlength=0') `
    -KeyAlgorithm RSA -KeyLength 2048 -HashAlgorithm SHA256 `
    -CertStoreLocation 'Cert:\CurrentUser\My' -NotAfter (Get-Date).AddYears(5)

$leaf = New-SelfSignedCertificate -Subject 'CN=localhost' -FriendlyName $leafName `
    -Signer $ca -KeyUsage DigitalSignature, KeyEncipherment `
    -TextExtension @('2.5.29.17={text}DNS=localhost&IPAddress=127.0.0.1&IPAddress=::1', '2.5.29.37={text}1.3.6.1.5.5.7.3.1') `
    -KeyAlgorithm RSA -KeyLength 2048 -HashAlgorithm SHA256 `
    -CertStoreLocation 'Cert:\CurrentUser\My' -NotAfter (Get-Date).AddDays(397)

$cer = Join-Path $env:TEMP 'EdgeLinkLocalCA.cer'
Export-Certificate -Cert $ca -FilePath $cer -Type CERT | Out-Null
Write-Host ''
Write-Host 'Windows will now ask whether to install "EdgeLink Local CA". Click Yes.' -ForegroundColor Yellow
Import-Certificate -FilePath $cer -CertStoreLocation 'Cert:\CurrentUser\Root' | Out-Null
Remove-Item $cer -Force

# Drop the CA private key: the trusted root can no longer sign anything new.
Remove-Item "Cert:\CurrentUser\My\$($ca.Thumbprint)" -DeleteKey

Write-Host ''
Write-Host "Done. localhost certificate $($leaf.Thumbprint) valid until $($leaf.NotAfter.ToString('yyyy-MM-dd'))." -ForegroundColor Green
Write-Host 'Restart Start-EdgeLink.cmd. Both http:// and https://localhost:8787 will work.'
