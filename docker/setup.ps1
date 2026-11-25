# First-time on-prem setup: docker/.env with random secrets + envelope key ring.
# Usage: ./docker/setup.ps1 [-PublicUrl https://tao.example.com]
param([string]$PublicUrl)
$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

function New-Secret { -join ([Security.Cryptography.RandomNumberGenerator]::GetBytes(24) | ForEach-Object { $_.ToString('x2') }) }

if (Test-Path .env) {
    Write-Warning 'docker/.env already exists: edit it manually (secrets are never regenerated).'
} else {
    $lines = Get-Content .env.example | ForEach-Object {
        $line = $_
        while ($line.Contains('REPLACE_WITH_RANDOM_SECRET')) {
            $index = $line.IndexOf('REPLACE_WITH_RANDOM_SECRET')
            $line = $line.Remove($index, 'REPLACE_WITH_RANDOM_SECRET'.Length).Insert($index, (New-Secret))
        }
        if ($PublicUrl -and $line.StartsWith('TAO_PUBLIC_URL=')) { $line = 'TAO_PUBLIC_URL=' + $PublicUrl.TrimEnd('/') }
        $line
    }
    [IO.File]::WriteAllLines((Join-Path $PSScriptRoot '.env'), [string[]]$lines)
    Write-Output 'Created docker/.env'
}

$secrets = Join-Path $PSScriptRoot 'secrets'
New-Item -ItemType Directory -Force -Path $secrets | Out-Null
$ring = Join-Path $secrets 'envelope.keyring.json'
if (-not (Test-Path $ring)) {
    $key = [Convert]::ToBase64String([Security.Cryptography.RandomNumberGenerator]::GetBytes(32))
    [IO.File]::WriteAllText($ring, "{`"activeKeyId`":`"k1`",`"keys`":{`"k1`":`"$key`"}}`n")
    Write-Output 'Created docker/secrets/envelope.keyring.json (back it up: without it notes cannot be decrypted)'
}
