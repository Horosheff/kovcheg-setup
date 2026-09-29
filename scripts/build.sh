#!/bin/bash
# Сборка win-x64 на Linux/macOS. Нужны python3 и .NET 8 SDK.
set -euo pipefail
ROOT=$(cd "$(dirname "$0")/.." && pwd)
cd "$ROOT"
ENV_FILE=${1:-$ROOT/client.env}
[[ -f $ENV_FILE ]] || { echo "нет $ENV_FILE — скачайте /root/kovcheg-client.env с VPS и не коммитьте его"; exit 1; }
python3 "$ROOT/scripts/restore-assets.py"
STAGE=$(mktemp -d)
trap 'rm -rf "$STAGE"' EXIT
cp -a "$ROOT/src" "$STAGE/src"
python3 "$ROOT/scripts/stamp.py" "$ENV_FILE" --root "$STAGE/src"
dotnet publish "$STAGE/src/KovchegVPN/KovchegVPN.csproj" -c Release -r win-x64 \
  --self-contained true -p:PublishSingleFile=true -p:EnableWindowsTargeting=true \
  -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true \
  -o "$ROOT/dist"
echo "готово: $ROOT/dist/KovchegVPN.exe"
