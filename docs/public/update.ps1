# JunimoServer update: pulls the configured image, installs the docker-compose.yml that matches
# it, and restarts. One command for latest, preview, and pinned IMAGE_VERSION.
#
#   powershell -c "irm https://docs.junimoserver.com/update.ps1 | iex"
#
# Run it from the directory that holds your docker-compose.yml and .env.
$ErrorActionPreference = 'Stop'

$repo = 'stardew-valley-dedicated-server/server'

function Die($msg) { throw $msg }

if (-not (Get-Command docker -ErrorAction SilentlyContinue)) { Die 'Docker is not installed or not on PATH.' }
docker compose version | Out-Null
if ($LASTEXITCODE -ne 0) { Die 'The Docker Compose plugin is required (docker compose v2).' }
if (-not (Test-Path docker-compose.yml)) { Die 'No docker-compose.yml here. Run this from your server directory, or install first: https://docs.junimoserver.com/install.ps1' }

Write-Host 'Pulling images...'
docker compose pull
if ($LASTEXITCODE -ne 0) { Die 'docker compose pull failed.' }

# Resolve the image tag Compose will actually use (honors .env and any override file).
$serverImage = docker compose config --images 2>$null | Where-Object { $_ -match '^sdvd/server:' } | Select-Object -First 1
if (-not $serverImage) { $serverImage = 'sdvd/server:latest' }

# The image bakes in the commit it was built from (docker/Dockerfile: ENV SDVD_GIT_SHA); fetch the
# docker-compose.yml from that exact commit so Compose always matches the image. Mirrors the
# resolution in docker/rootfs/startapp.sh (raw@sha, else the latest release).
$envLines = docker image inspect $serverImage --format '{{range .Config.Env}}{{println .}}{{end}}' 2>$null
$sha = $envLines | ForEach-Object { if ($_ -match '^SDVD_GIT_SHA=(.*)$') { $matches[1] } } | Select-Object -First 1
if ($sha -and $sha -ne 'unknown') {
    $composeUrl = "https://raw.githubusercontent.com/$repo/$sha/docker-compose.yml"
} else {
    $composeUrl = "https://github.com/$repo/releases/latest/download/docker-compose.yml"
}

Write-Host 'Fetching docker-compose.yml...'
Invoke-WebRequest -UseBasicParsing -Uri $composeUrl -OutFile docker-compose.yml

Write-Host 'Restarting...'
docker compose up -d --remove-orphans
if ($LASTEXITCODE -ne 0) { Die 'docker compose up failed.' }
Write-Host 'Update complete.'
