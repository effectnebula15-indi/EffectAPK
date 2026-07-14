# Сборка установщика EffectAPK: dotnet publish (self-contained) + WiX v5 MSI.
# Результат: out\EffectApkSetup.msi. Прав администратора не требуется ни для сборки, ни для установки.
param([string]$Configuration = "Release")
$ErrorActionPreference = "Stop"

$root = Split-Path $PSScriptRoot -Parent
$publishDir = Join-Path $root "out\publish"
$iconPath = Join-Path $root "src\EffectApk.App\Assets\EffectApk.ico"
$msiPath = Join-Path $root "out\EffectApkSetup.msi"

Write-Host "== dotnet publish (self-contained win-x64) =="
dotnet publish (Join-Path $root "src\EffectApk.App") -c $Configuration -r win-x64 --self-contained true -o $publishDir
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed" }

Write-Host "== WiX toolset =="
# Пин на v5: начиная с v7 WiX требует принятия OSMF EULA, v5 — нет
$wixVersion = "5.0.2"
$installed = (Get-Command wix -ErrorAction SilentlyContinue) -and ((wix --version) -like "5.*")
if (-not $installed) {
    dotnet tool uninstall --global wix 2>$null | Out-Null
    dotnet tool install --global wix --version $wixVersion
    if ($LASTEXITCODE -ne 0) { throw "wix tool install failed" }
}
# Расширение UI той же мажорной версии (идемпотентно)
wix extension add -g "WixToolset.UI.wixext/$wixVersion" 2>$null | Out-Null

Write-Host "== wix build =="
# ICE-валидацию не запускаем (в v5 это отдельная команда `wix msi validate`):
# per-user MSI с файлами в LocalAppData формально нарушает ICE38 — это штатная
# схема установки без прав администратора (так делает, например, VS Code)
wix build (Join-Path $PSScriptRoot "EffectApk.wxs") `
    -ext WixToolset.UI.wixext `
    -d "PublishDir=$publishDir" `
    -d "IconPath=$iconPath" `
    -arch x64 `
    -o $msiPath
if ($LASTEXITCODE -ne 0) { throw "wix build failed" }

Write-Host "OK: $msiPath ($([math]::Round((Get-Item $msiPath).Length / 1MB, 1)) MB)"
