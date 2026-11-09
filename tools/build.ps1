param([ValidatePattern('^Kuro(?:-[0-9]+(?:\.[0-9]+)*)?$')][string]$PackageName = 'Kuro-0.1.5')
$ErrorActionPreference = 'Stop'
Set-Location -LiteralPath (Split-Path -Parent $PSScriptRoot)
$outputDirectory = Join-Path 'dist' $PackageName
$archive = Join-Path 'dist' "$PackageName-win-x64.zip"
dotnet publish src/Kuro/Kuro.csproj -c Release -r win-x64 --self-contained false -o $outputDirectory --nologo
if ($LASTEXITCODE -ne 0) { throw 'Kuro publish failed.' }
if (Test-Path -LiteralPath 'True Motion' -PathType Container) {
    Copy-Item -LiteralPath 'True Motion' -Destination $outputDirectory -Recurse -Force
}
Copy-Item -LiteralPath 'examples' -Destination $outputDirectory -Recurse -Force
Copy-Item -LiteralPath 'docs' -Destination $outputDirectory -Recurse -Force
Copy-Item -LiteralPath 'README.md' -Destination (Join-Path $outputDirectory 'README.md') -Force
Copy-Item -LiteralPath 'LICENSE' -Destination $outputDirectory -Force
Compress-Archive -LiteralPath $outputDirectory -DestinationPath $archive -Force
Write-Output "Built $outputDirectory/Kuro.exe and $archive. .NET 8 Desktop Runtime and local FFmpeg are required."
