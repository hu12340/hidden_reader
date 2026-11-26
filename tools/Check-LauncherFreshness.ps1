# Prints 1 when the launcher should rebuild (exe missing or any source newer than the exe),
# 0 otherwise. ASCII-only script (BOM-exempt per OPERATING-NOTES).
param([Parameter(Mandatory = $true)][string]$ExePath)
$ErrorActionPreference = 'Stop'

if (-not (Test-Path -LiteralPath $ExePath)) { Write-Output 1; exit 0 }

$repo = Split-Path -Parent $PSScriptRoot   # tools\ -> repo root
$src = Join-Path $repo 'src'
$cut = (Get-Item -LiteralPath $ExePath).LastWriteTime

# obj/bin excluded: their generated files refresh on every build (would always look stale).
$newest = Get-ChildItem -LiteralPath $src -Recurse -File |
    Where-Object { $_.FullName -notmatch '\\(obj|bin)\\' -and $_.Extension -match '^\.(cs|xaml|csproj|ico|json|manifest)$' } |
    Sort-Object -Property LastWriteTime -Descending |
    Select-Object -First 1

if ($newest -and $newest.LastWriteTime -gt $cut) { Write-Output 1 } else { Write-Output 0 }
