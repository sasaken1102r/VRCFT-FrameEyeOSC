# FrameEyeOSC.dll と module.json を 1 つの zip にまとめる（VRCFT の「Install from .zip」用）。
# 使い方: pwsh tools/package.ps1
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$moduleJson = Join-Path $root 'module.json'
$csproj = Join-Path $root 'FrameEyeOSC/FrameEyeOSC.csproj'
$meta = Get-Content $moduleJson -Raw | ConvertFrom-Json

# module.json の Version と csproj の <Version> がずれていたら止める。
$projectVersion = (Select-Xml -Path $csproj -XPath '//Version').Node.InnerText
if ($meta.Version -ne $projectVersion) {
    throw "module.json の Version ($($meta.Version)) と FrameEyeOSC.csproj の <Version> ($projectVersion) が違います"
}

dotnet build $csproj -c Release -p:ContinuousIntegrationBuild=true
if ($LASTEXITCODE -ne 0) { throw 'build failed' }

$dll = Join-Path $root 'FrameEyeOSC/bin/Release/net8.0/FrameEyeOSC.dll'
$stage = Join-Path $root 'dist/stage'
if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
New-Item -ItemType Directory -Force $stage | Out-Null
Copy-Item $dll $stage

# zip の直下に module.json を置く。LastUpdated だけビルドした日（UTC）にする。リポジトリのファイルは書き換えない。
$built = [DateTime]::UtcNow.ToString('yyyy-MM-dd') + 'T00:00:00Z'
$pattern = '("LastUpdated"\s*:\s*")[^"]*(")'
$text = Get-Content $moduleJson -Raw
if ([regex]::Matches($text, $pattern).Count -ne 1) { throw 'module.json に LastUpdated が 1 つだけあるはずです' }
$text = [regex]::Replace($text, $pattern, "`${1}$built`${2}")
[IO.File]::WriteAllText((Join-Path $stage 'module.json'), $text, [Text.UTF8Encoding]::new($false))

$zip = Join-Path $root "dist/FrameEyeOSC_$($meta.Version).zip"
if (Test-Path $zip) { Remove-Item $zip -Force }
Compress-Archive -Path (Join-Path $stage '*') -DestinationPath $zip
Remove-Item $stage -Recurse -Force
Write-Host "wrote $zip"
(Get-FileHash $zip -Algorithm SHA256).Hash
