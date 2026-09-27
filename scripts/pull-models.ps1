<#
.SYNOPSIS
    Downloads models into the ScanHarness Ollama container (see docker-compose.yml).

.EXAMPLE
    ./scripts/pull-models.ps1
    ./scripts/pull-models.ps1 -Models qwen2.5:7b,llama3.1:8b
#>
param(
    # Small models that support tool calling and run acceptably on CPU.
    [string[]] $Models = @("qwen2.5:3b", "llama3.2:3b", "llama3.2:1b"),
    [string] $Container = "scanharness-ollama"
)

$ErrorActionPreference = "Stop"

$running = docker ps --filter "name=^$Container$" --format "{{.Names}}"
if (-not $running) {
    Write-Error "Container '$Container' is not running. Start it with: docker compose up -d"
}

foreach ($model in $Models) {
    Write-Host "Pulling $model ..." -ForegroundColor Cyan
    docker exec $Container ollama pull $model
    if ($LASTEXITCODE -ne 0) { Write-Error "Failed to pull $model" }
}

Write-Host "`nInstalled models:" -ForegroundColor Cyan
docker exec $Container ollama list
