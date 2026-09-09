# JunimoServer installer: scaffolds a server directory with docker-compose.yml and .env.
#
#   powershell -c "irm https://docs.junimoserver.com/install.ps1 | iex"
#
# Set $env:DIR to choose the directory (default .\junimoserver). It does not start the server:
# add your Steam credentials to .env first (see the printed next steps).
$ErrorActionPreference = 'Stop'

$repo = 'stardew-valley-dedicated-server/server'
$base = "https://github.com/$repo/releases/latest/download"
$dir = if ($env:DIR) { $env:DIR } else { 'junimoserver' }

function Die($msg) { throw $msg }

if (-not (Get-Command docker -ErrorAction SilentlyContinue)) { Die 'Docker is not installed or not on PATH.' }
docker compose version | Out-Null
if ($LASTEXITCODE -ne 0) { Die 'The Docker Compose plugin is required (docker compose v2).' }

New-Item -ItemType Directory -Force -Path $dir | Out-Null
Write-Host "Installing into $((Resolve-Path $dir).Path)"

Invoke-WebRequest -UseBasicParsing -Uri "$base/docker-compose.yml" -OutFile (Join-Path $dir 'docker-compose.yml')

$envPath = Join-Path $dir '.env'
if (Test-Path $envPath) {
    Write-Host 'Keeping existing .env (not overwritten).'
} else {
    Invoke-WebRequest -UseBasicParsing -Uri "$base/.env.example" -OutFile $envPath
    Write-Host 'Wrote .env from .env.example.'
}

Write-Host @"

JunimoServer files are ready in $((Resolve-Path $dir).Path).

Next steps:
  1. cd $dir
  2. Edit .env and set at least STEAM_USERNAME, STEAM_PASSWORD, and VNC_PASSWORD.
  3. Authenticate with Steam:  docker compose run --rm -it steam-auth setup
  4. Start the server:         docker compose up -d

Update later with:  powershell -c "irm https://docs.junimoserver.com/update.ps1 | iex"
"@
