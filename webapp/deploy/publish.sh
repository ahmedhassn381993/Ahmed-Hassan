#!/usr/bin/env bash
# Builds the screens and the API into one folder and a zip for App Service zip deployment:
#   ./deploy/publish.sh            -> webapp/artifacts/workload-portal.zip
#   az webapp deploy -g <rg> -n <web app> --src-path artifacts/workload-portal.zip --type zip
set -euo pipefail
cd "$(dirname "$0")/.."
(cd frontend && npm ci --no-audit --no-fund && npm run build)
dotnet test --nologo
rm -rf artifacts/publish && dotnet publish src/Enppi.Workload.Api -c Release -o artifacts/publish --nologo
(cd artifacts/publish && rm -f ../workload-portal.zip && zip -qr ../workload-portal.zip .)
echo "artifacts/workload-portal.zip"
