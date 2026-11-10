param([string]$Compiler = 'ISCC.exe')
$ErrorActionPreference = 'Stop'
Set-Location -LiteralPath (Split-Path -Parent $PSScriptRoot)
$version = ([xml](Get-Content -LiteralPath 'src/Kuro/Kuro.csproj' -Raw)).Project.PropertyGroup.Version
$payload = Join-Path (Get-Location) "dist/installer-payload-$version"
dotnet publish src/Kuro/Kuro.csproj -c Release -r win-x64 --self-contained true -o $payload --nologo
if ($LASTEXITCODE) { exit $LASTEXITCODE }
Copy-Item -LiteralPath 'LICENSE','README.md' -Destination $payload -Force
$samples = Join-Path $payload 'examples'
New-Item -ItemType Directory -Path $samples -Force | Out-Null
Copy-Item -LiteralPath 'examples/First light.kuro','examples/First light.mp4','examples/First light.png' -Destination $samples -Force
$notices = Join-Path $payload 'Third-party notices'
New-Item -ItemType Directory -Path $notices -Force | Out-Null
Copy-Item -LiteralPath 'tools/notices/Markdig.txt' -Destination $notices -Force
$assets = Get-Content -LiteralPath 'src/Kuro/obj/project.assets.json' -Raw | ConvertFrom-Json
$packages = @($assets.libraries.PSObject.Properties.Name)
foreach ($framework in $assets.project.frameworks.PSObject.Properties.Value) {
    foreach ($dependency in $framework.downloadDependencies) {
        $packVersion = $dependency.version.TrimStart('[').Split(',')[0].TrimEnd(']')
        $packages += "$($dependency.name)/$packVersion"
    }
}
foreach ($package in ($packages | Select-Object -Unique)) {
    $packageId = $package.Split('/')[0]
    foreach ($root in $assets.packageFolders.PSObject.Properties.Name) {
        $folder = Join-Path $root $package.ToLowerInvariant()
        if (!(Test-Path -LiteralPath $folder -PathType Container)) { continue }
        Get-ChildItem -LiteralPath $folder -File | Where-Object Name -Match '^(license|third-?party-?notices)(\.(txt|md))?$' | ForEach-Object {
            Copy-Item -LiteralPath $_.FullName -Destination (Join-Path $notices "$packageId-$($_.Name)") -Force
        }
        break
    }
}
& $Compiler '/Q' "/DAppVersion=$version" "/DPayload=$payload" 'tools/installer.iss'
if ($LASTEXITCODE) { exit $LASTEXITCODE }
Write-Output "Built dist/OpenKuro-$version-Setup-x64.exe"
