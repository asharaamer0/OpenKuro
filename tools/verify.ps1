$ErrorActionPreference = 'Stop'
Set-Location -LiteralPath (Split-Path -Parent $PSScriptRoot)
dotnet build Kuro.sln -c Release --nologo
if ($LASTEXITCODE -ne 0) { throw 'Kuro build failed.' }
$test = Start-Process -FilePath './src/Kuro/bin/Release/net8.0-windows/Kuro.exe' -ArgumentList '--self-test' -WindowStyle Hidden -Wait -PassThru
if ($test.ExitCode -ne 0) { throw 'Verification failed. Read artifacts/test-failure.txt.' }
Get-Content -LiteralPath 'artifacts/verification-report.json'
