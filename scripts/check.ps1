$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
Push-Location -LiteralPath $projectRoot
try {
    docker compose up -d --wait --wait-timeout 600
    if ($LASTEXITCODE -ne 0) { throw 'Cassandra startup failed.' }
    dotnet test ReservationService.sln --configuration Release
    if ($LASTEXITCODE -ne 0) { throw 'Integration tests failed.' }
    dotnet run --project samples/ReservationService.Labs --configuration Release -- all
    if ($LASTEXITCODE -ne 0) { throw 'Cassandra labs failed.' }
}
finally { Pop-Location }
