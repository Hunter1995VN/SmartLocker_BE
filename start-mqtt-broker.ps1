$ErrorActionPreference = 'Stop'
Push-Location $PSScriptRoot
try {
    if (!(Test-Path 'scripts/local-broker/node_modules/aedes')) {
        & pnpm install --dir scripts/local-broker --frozen-lockfile --ignore-scripts
        if ($LASTEXITCODE -ne 0) { throw 'Cannot install local broker dependencies.' }
    }
    & node scripts/local-broker/broker.mjs
    exit $LASTEXITCODE
} finally { Pop-Location }
