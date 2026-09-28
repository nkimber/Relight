param(
    [string]$OutputRoot = (Join-Path $PSScriptRoot '..\artifacts\releases')
)

$ErrorActionPreference = 'Stop'
$repository = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).Path
if (git -C $repository status --porcelain) {
    throw 'Commit the source and documentation before building a portable preview.'
}
$revision = (git -C $repository rev-parse HEAD).Trim()
$project = Join-Path $repository 'src\Relight.App\Relight.App.csproj'
[xml]$projectXml = Get-Content -LiteralPath $project
$version = [string]$projectXml.Project.PropertyGroup.Version
if ([string]::IsNullOrWhiteSpace($version)) { throw 'Relight version metadata is missing.' }
$destination = Join-Path $OutputRoot ("Relight-$version-$($revision.Substring(0,7))-win-x64-preview")
if (Test-Path -LiteralPath $destination) { throw "Output already exists: $destination" }
$destination = [System.IO.Path]::GetFullPath($destination)
$publish = Join-Path $destination 'publish'
New-Item -ItemType Directory -Path $publish -Force | Out-Null

dotnet publish $project -c Release -r win-x64 --self-contained true -o $publish
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed.' }
$executable = Join-Path $publish 'Relight.exe'
$depsPath = Join-Path $publish 'Relight.deps.json'
if (-not (Test-Path -LiteralPath $executable) -or -not (Test-Path -LiteralPath $depsPath)) {
    throw 'The published application is incomplete.'
}
$deps = Get-Content -LiteralPath $depsPath -Raw | ConvertFrom-Json
$libraries = @($deps.libraries.PSObject.Properties.Name)
function Get-Version([string]$prefix) {
    $matches = @($libraries | Where-Object { $_.StartsWith("$prefix/", [System.StringComparison]::OrdinalIgnoreCase) })
    if ($matches.Count -ne 1) { throw "Expected one published dependency for $prefix." }
    return $matches[0].Substring($prefix.Length + 1)
}
$coreVersion = Get-Version 'runtimepack.Microsoft.NETCore.App.Runtime.win-x64'
$desktopVersion = Get-Version 'runtimepack.Microsoft.WindowsDesktop.App.Runtime.win-x64'
$managementVersion = Get-Version 'System.Management'
$packageRoot = Join-Path $env:USERPROFILE '.nuget\packages'
$noticeRoot = Join-Path $publish 'Notices'
New-Item -ItemType Directory -Path $noticeRoot | Out-Null
$core = Join-Path $packageRoot "microsoft.netcore.app.runtime.win-x64\$coreVersion"
$desktop = Join-Path $packageRoot "microsoft.windowsdesktop.app.runtime.win-x64\$desktopVersion"
$management = Join-Path $packageRoot "system.management\$managementVersion"
$sources = @(
    @{ Source = (Join-Path $core 'LICENSE.TXT'); Name = 'NET-Runtime-LICENSE.txt' },
    @{ Source = (Join-Path $core 'THIRD-PARTY-NOTICES.TXT'); Name = 'NET-Runtime-THIRD-PARTY-NOTICES.txt' },
    @{ Source = (Join-Path $desktop 'LICENSE'); Name = 'WindowsDesktop-Runtime-LICENSE.txt' },
    @{ Source = (Join-Path $management 'system.management.nuspec'); Name = 'System.Management.nuspec' }
)
foreach ($entry in $sources) {
    if (-not (Test-Path -LiteralPath $entry.Source)) {
        throw "Required dependency notice is missing: $($entry.Source)"
    }
    Copy-Item -LiteralPath $entry.Source -Destination (Join-Path $noticeRoot $entry.Name)
}
$notice = @"
Relight portable preview dependency notices

Source revision: $revision
Application version: $version
Target runtime: win-x64, self-contained

Microsoft.NETCore.App.Runtime.win-x64 $coreVersion — MIT; see NET-Runtime-LICENSE.txt and NET-Runtime-THIRD-PARTY-NOTICES.txt.
Microsoft.WindowsDesktop.App.Runtime.win-x64 $desktopVersion — MIT; see WindowsDesktop-Runtime-LICENSE.txt.
System.Management $managementVersion — MIT license expression in System.Management.nuspec; the MIT text is in NET-Runtime-LICENSE.txt.

These entries come from Relight.deps.json and the corresponding local NuGet package metadata. Relight is a development preview, not an accepted unattended release.
"@
Set-Content -LiteralPath (Join-Path $noticeRoot 'DEPENDENCIES.txt') -Value $notice -Encoding utf8
Copy-Item -LiteralPath (Join-Path $repository 'README.md') -Destination (Join-Path $publish 'README.md')
pwsh -File (Join-Path $repository 'scripts\Test-Shell.ps1') -Executable $executable
if ($LASTEXITCODE -ne 0) { throw 'Published shell smoke test failed.' }

$archive = Join-Path $destination "Relight-$version-win-x64-preview.zip"
[System.IO.Compression.ZipFile]::CreateFromDirectory($publish, $archive)
$hash = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant()
$checksum = "$hash *$(Split-Path -Leaf $archive)"
Set-Content -LiteralPath (Join-Path $destination 'SHA256SUMS.txt') -Value $checksum -Encoding ascii
$manifest = [ordered]@{
    application = 'Relight'
    version = $version
    revision = $revision
    runtime = 'win-x64'
    selfContained = $true
    dotnetSdk = (dotnet --version).Trim()
    coreRuntime = $coreVersion
    desktopRuntime = $desktopVersion
    systemManagement = $managementVersion
    archive = (Split-Path -Leaf $archive)
    sha256 = $hash
    generatedAtUtc = [DateTimeOffset]::UtcNow.ToString('O')
}
$manifest | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $destination 'manifest.json') -Encoding utf8
Write-Output "Portable preview: $archive"
Write-Output "SHA-256: $hash"
Write-Output "Manifest: $(Join-Path $destination 'manifest.json')"
