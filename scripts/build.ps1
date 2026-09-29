# Сборка KovchegVPN.exe на Windows. Нужен .NET 8 SDK и Python 3.
$ErrorActionPreference = "Stop"
$Root = Split-Path -Parent $PSScriptRoot
Set-Location $Root

$EnvFile = Join-Path $Root "client.env"
if (-not (Test-Path $EnvFile)) {
    throw "Нет client.env. Это файл, который server/install.sh написал на VPS в /root/kovcheg-client.env. Скачайте его сюда и не коммитьте."
}

python (Join-Path $Root "scripts/restore-assets.py")
$Stage = Join-Path $env:TEMP ("kovcheg-build-" + [guid]::NewGuid().ToString("n"))
New-Item -ItemType Directory -Path $Stage | Out-Null
try {
    Copy-Item -Recurse (Join-Path $Root "src") (Join-Path $Stage "src")
    python (Join-Path $Root "scripts/stamp.py") $EnvFile --root (Join-Path $Stage "src")
    dotnet publish (Join-Path $Stage "src/KovchegVPN/KovchegVPN.csproj") -c Release -r win-x64 `
        --self-contained true -p:PublishSingleFile=true -p:EnableWindowsTargeting=true `
        -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true `
        -o (Join-Path $Root "dist")
}
finally {
    Remove-Item -Recurse -Force $Stage -ErrorAction SilentlyContinue
}
Write-Host "готово: $Root\dist\KovchegVPN.exe"
Write-Host "залейте его на VPS: scp dist/KovchegVPN.exe root@SERVER:/var/www/html/KovchegVPN.exe"
Write-Host "затем на VPS: bash /root/kovcheg/publish-release.sh 1.0.0 `"первая сборка`""
