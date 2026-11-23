$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
Push-Location -LiteralPath $projectRoot
try {
    docker compose up -d --wait --wait-timeout 600
    if ($LASTEXITCODE -ne 0) { throw 'Cassandra startup failed. Check docker compose logs cassandra.' }
    dotnet run --project src/ReservationService.Api --launch-profile http
    if ($LASTEXITCODE -ne 0) { throw 'API startup failed. Check the error above.' }
}
finally { Pop-Location }
