# JunimoServer install/update: one script for both.
#
#   powershell -c "irm https://docs.junimoserver.com/install.ps1 | iex"
#
# Run it in your server folder to update in place, or in an empty folder to set up a new server
# (it scaffolds .\junimoserver). Set $env:DIR to choose the new-server directory.
#
# A fresh install generates a secure API key, then (interactively) offers to sign you in to Steam
# and start the server. Update confirms the release channel and asks whether to restart.
# Non-interactive callers skip all prompts: set $env:IMAGE_VERSION=preview|latest to choose the
# channel, or $env:NO_TTY=1 to keep the current channel (recommended on a fresh install) and restart.
$ErrorActionPreference = 'Stop'

$repo = 'stardew-valley-dedicated-server/server'
$master = "https://raw.githubusercontent.com/$repo/master"

# Channel recommended to new installs. Preview is recommended while a working stable ('latest')
# release is still being finalized; flip to 'stable' once latest ships.
$RecommendedChannel = 'preview'
# Shown in the channel prompt so users don't reflexively pick stable. Clear it once stable is ready.
$RecommendedNote = "stable isn't ready yet"
$NoTty = ($env:NO_TTY -eq '1')

function Die($msg) { throw $msg }

# True when we may prompt: a console is attached and the caller didn't opt out with $env:NO_TTY.
function Test-Interactive { -not $NoTty -and [Environment]::UserInteractive }

# Prompt on the console and return the reply, trimmed and lowercased.
function Read-Reply($prompt) { (Read-Host $prompt).Trim().ToLower() }

# The IMAGE_VERSION value for the recommended channel (stable -> latest, otherwise preview).
function Get-RecommendedVersion { if ($RecommendedChannel -eq 'stable') { 'latest' } else { 'preview' } }

# The raw.githubusercontent base URL for the commit an already-pulled image was built from
# (docker/Dockerfile bakes it as ENV SDVD_GIT_SHA), or master when unknown. This is how
# docker-compose.yml / .env.example are fetched to match the image, without needing releases.
function Get-RawBaseForImage($image) {
    $sha = $null
    try {
        $envLines = docker image inspect $image --format '{{range .Config.Env}}{{println .}}{{end}}' 2>$null
        $sha = $envLines | ForEach-Object { if ($_ -match '^SDVD_GIT_SHA=(.*)$') { $matches[1] } } | Select-Object -First 1
    } catch { }
    # Only a plain commit hash resolves on GitHub; local builds stamp "unknown" or "<sha>-dirty".
    if ($sha -match '^[0-9a-f]+$') { "https://raw.githubusercontent.com/$repo/$sha" } else { $master }
}

# The uncommented value of $key in an env file; '' if only commented or absent.
function Get-EnvValue($file, $key) {
    $match = Select-String -Path $file -Pattern "^\s*$key=[""']?([^""'#\s]*)" | Select-Object -First 1
    if ($match) { $match.Matches[0].Groups[1].Value } else { '' }
}

# The sorted, unique variable names declared in an env file (commented or not).
function Get-EnvKeys($file) {
    Select-String -Path $file -Pattern '^\s*#?\s*([A-Za-z_][A-Za-z0-9_]*)\s*=' |
        ForEach-Object { $_.Matches[0].Groups[1].Value } | Sort-Object -Unique
}

# Generate a random hex token (32 bytes -> 64 hex chars) for secure-by-default secrets.
function New-SecretKey {
    $bytes = New-Object 'byte[]' 32
    [System.Security.Cryptography.RandomNumberGenerator]::Create().GetBytes($bytes)
    -join ($bytes | ForEach-Object { $_.ToString('x2') })
}

# Set KEY=VALUE in an env file, replacing the first commented-or-uncommented KEY= line, else appending.
function Set-EnvVar($file, $key, $value) {
    $lines = @(Get-Content -Path $file)
    $pattern = "^\s*#?\s*$key="
    $replaced = $false
    # @(...) forces an array so a single-line file doesn't collapse $out to a string (then += would concatenate).
    $out = @(foreach ($line in $lines) {
        if (-not $replaced -and $line -match $pattern) { "$key=$value"; $replaced = $true } else { $line }
    })
    if (-not $replaced) { $out += "$key=$value" }
    Set-Content -Path $file -Value $out -Encoding utf8
}

# Resolve the IMAGE_VERSION to write. $mode is install|update; $current is the existing value (update).
# Precedence: explicit $env:IMAGE_VERSION > interactive prompt > keep current (update) / recommended.
function Resolve-ChannelVersion($mode, $current) {
    if ($env:IMAGE_VERSION) { return $env:IMAGE_VERSION }
    if (-not (Test-Interactive)) {
        if ($mode -eq 'update' -and $current) { return $current }
        return (Get-RecommendedVersion)
    }
    if ($mode -eq 'update') {
        $default = if ($current) { $current } else { Get-RecommendedVersion }
        $reply = Read-Reply "Release channel? preview or stable [$default]"
    } else {
        $default = Get-RecommendedVersion
        $note = if ($RecommendedNote) { "; $RecommendedNote" } else { '' }
        $hint = if ($RecommendedChannel -eq 'preview') { "preview (recommended$note) or stable [preview]" } else { "preview or stable (recommended$note) [stable]" }
        $reply = Read-Reply "Release channel? $hint"
    }
    switch ($reply) {
        { $_ -in 'p', 'preview' } { return 'preview' }
        { $_ -in 's', 'stable' }  { return 'latest' }
        default { return $default }
    }
}

# Ask whether to restart now to apply a staged update. Non-interactive runs return $true so that
# unattended updates still apply; interactively the default is yes (Enter).
function Confirm-Restart {
    if (-not (Test-Interactive)) { return $true }
    $reply = Read-Reply 'Restart now to apply the update? [Y/n]'
    return -not ($reply -eq 'n' -or $reply -eq 'no')
}

# Turn a Die (throw) into a clean one-line error instead of a PowerShell stack trace, without exit
# (which would close an `irm | iex` session).
try {

if (-not (Get-Command docker -ErrorAction SilentlyContinue)) { Die 'Docker is not installed or not on PATH.' }
docker compose version | Out-Null
if ($LASTEXITCODE -ne 0) { Die 'The Docker Compose plugin is required (docker compose v2).' }

# Update in place when the current directory already holds a server, else scaffold a new one.
if (Test-Path docker-compose.yml) {
    $target = (Get-Location).Path
} else {
    $target = if ($env:DIR) { $env:DIR } else { 'junimoserver' }
    New-Item -ItemType Directory -Force -Path $target | Out-Null
}
# Push into the target so the script never leaves the caller's session in a different directory,
# even on abort (Pop-Location in the finally restores it).
Push-Location $target
try {

if (Test-Path docker-compose.yml) {
    # -- Update -----------------------------------------------------------------------------------
    Write-Host "Updating server in $((Get-Location).Path)"
    Write-Host ''

    # Confirm the channel before pulling: IMAGE_VERSION decides which image Compose fetches.
    $currentVersion = ''
    if (Test-Path .env) { $currentVersion = Get-EnvValue '.env' 'IMAGE_VERSION' }
    if (-not $currentVersion) { $currentVersion = 'latest' }
    $channelVersion = Resolve-ChannelVersion 'update' $currentVersion
    if ($channelVersion -ne $currentVersion) {
        Write-Host "Switching channel: $currentVersion -> $channelVersion"
        Write-Host 'Back up your saves first; save formats can differ between builds.'
        if (Test-Path .env) { Set-EnvVar '.env' 'IMAGE_VERSION' $channelVersion }
    }
    Write-Host "Channel: $channelVersion"
    Write-Host ''

    Write-Host 'Pulling images...'
    docker compose pull
    if ($LASTEXITCODE -ne 0) { Die 'docker compose pull failed.' }
    Write-Host ''

    # Fetch the docker-compose.yml matching the image we just pulled (honors .env and any override).
    $serverImage = docker compose config --images 2>$null | Where-Object { $_ -match '^sdvd/server:' } | Select-Object -First 1
    if (-not $serverImage) { $serverImage = 'sdvd/server:latest' }
    $rawBase = Get-RawBaseForImage $serverImage

    Write-Host 'Fetching docker-compose.yml...'
    $tmpCompose = New-TemporaryFile
    Invoke-WebRequest -UseBasicParsing -Uri "$rawBase/docker-compose.yml" -OutFile $tmpCompose
    Move-Item $tmpCompose docker-compose.yml -Force
    Write-Host 'Updated docker-compose.yml to match the new image.'

    # New options ship in .env.example but your .env is never touched, so surface any you're missing.
    # Keys are matched commented or not, so an option you deliberately left commented isn't re-flagged.
    if (Test-Path .env) {
        $tmpExample = New-TemporaryFile
        try {
            Invoke-WebRequest -UseBasicParsing -Uri "$rawBase/.env.example" -OutFile $tmpExample
            $newKeys = Get-EnvKeys $tmpExample | Where-Object { $_ -notin (Get-EnvKeys '.env') }
            if ($newKeys) {
                Write-Host ''
                Write-Host 'New .env options are available this release (your .env is unchanged):'
                $newKeys | ForEach-Object { Write-Host "  - $_" }
                Write-Host 'Add any you want to your .env. Docs: https://docs.junimoserver.com/admins/configuration/environment'
            }
        } catch {
            # Non-fatal: if the matching .env.example can't be fetched, skip the heads-up.
        } finally {
            Remove-Item $tmpExample -Force -ErrorAction SilentlyContinue
        }
    }
    Write-Host ''

    if (Confirm-Restart) {
        Write-Host 'Restarting...'
        docker compose up -d --remove-orphans
        if ($LASTEXITCODE -ne 0) { Die 'docker compose up failed.' }
        Write-Host ''
        Write-Host 'Update complete.'
    } else {
        Write-Host "Update staged. Apply it when you're ready with:"
        Write-Host '  docker compose up -d'
    }
    Write-Host ''
} else {
    # -- Fresh install ----------------------------------------------------------------------------
    Write-Host "Installing into $((Get-Location).Path)"
    Write-Host ''

    # Pick the channel first, then pull its server image so we can fetch the matching config.
    if (Test-Path .env) {
        $channelVersion = Get-EnvValue '.env' 'IMAGE_VERSION'
        if (-not $channelVersion) { $channelVersion = 'latest' }
    } else {
        $channelVersion = Resolve-ChannelVersion 'install' ''
    }
    $serverImage = "sdvd/server:$channelVersion"

    Write-Host "Pulling $serverImage..."
    docker pull $serverImage
    if ($LASTEXITCODE -ne 0) {
        # An image that is already present locally (a `make build` tag, or an offline host) is fine.
        if (-not (docker image ls -q $serverImage)) { Die "Could not pull $serverImage. Check the channel/version and your network." }
        Write-Host "Using the local $serverImage image."
    }
    $rawBase = Get-RawBaseForImage $serverImage
    Write-Host ''

    Invoke-WebRequest -UseBasicParsing -Uri "$rawBase/docker-compose.yml" -OutFile 'docker-compose.yml'
    if (Test-Path .env) {
        Write-Host "Wrote docker-compose.yml. Kept existing .env (IMAGE_VERSION=$channelVersion, not overwritten)."
    } else {
        Invoke-WebRequest -UseBasicParsing -Uri "$rawBase/.env.example" -OutFile '.env'
        Set-EnvVar '.env' 'IMAGE_VERSION' $channelVersion
        # Secure by default: a strong random API key so the HTTP API is never left open.
        Set-EnvVar '.env' 'API_KEY' (New-SecretKey)
        # TODO: remove once a published image no longer refuses to start without VNC_PASSWORD
        # (the image-side gate now disables the VNC ports instead). Until then a fresh .env
        # without it crash-loops the server.
        Set-EnvVar '.env' 'VNC_PASSWORD' ((New-SecretKey).Substring(0, 16))
        Write-Host "Wrote docker-compose.yml and .env (IMAGE_VERSION=$channelVersion, API_KEY and VNC_PASSWORD generated)."
    }

    # Offer to finish now: sign in to Steam, start, open the console. Non-interactive prints steps.
    Write-Host ''
    $setupNow = if (Test-Interactive) { Read-Reply 'Sign in to Steam and start the server now? [Y/n]' } else { 'n' }
    if ($setupNow -eq 'n' -or $setupNow -eq 'no') {
        Write-Host @"

JunimoServer is installed in $((Get-Location).Path). To finish:

  1. cd $target
  2. Sign in to Steam (enter your account; downloads the game):
       docker compose run --rm -it steam-auth setup
  3. Start the server:  docker compose up -d
  4. Open the console:  docker compose exec server attach-cli   (type 'info' for your invite code)

Update later by re-running this in the same folder:
  powershell -c "irm https://docs.junimoserver.com/install.ps1 | iex"

"@
    } else {
        Write-Host 'Sign in to your Steam account when prompted (Steam Guard may ask for a code).'
        docker compose run --rm -it steam-auth setup
        if ($LASTEXITCODE -ne 0) { Die 'Steam sign-in did not complete.' }
        Write-Host ''
        Write-Host 'Starting the server...'
        docker compose up -d
        if ($LASTEXITCODE -ne 0) { Die 'docker compose up failed.' }
        Write-Host ''
        Write-Host 'Waiting for the server to be ready. First boot downloads and loads the game,'
        Write-Host 'so this can take a few minutes (watch it with: docker compose logs -f steam-auth).'

        # Wait until the server is ready (its game log appears) or crashes, whichever comes first.
        # Attaching into a crashed/crash-looping server hangs forever, so surface the logs instead.
        # A crash shows as State.Status != running or a climbing RestartCount (not a clean exit).
        $cid = docker compose ps -aq server 2>$null | Select-Object -First 1
        $r0 = docker inspect -f '{{.RestartCount}}' $cid 2>$null
        $restarts0 = if ($r0) { [int]$r0 } else { 0 }
        $waited = 0
        while ($waited -lt 600) {
            $status = docker inspect -f '{{.State.Status}}' $cid 2>$null
            $r = docker inspect -f '{{.RestartCount}}' $cid 2>$null
            $restarts = if ($r) { [int]$r } else { 0 }
            if ($status -ne 'running' -or $restarts -gt $restarts0) {
                Write-Host ''
                Write-Host 'The server stopped right after starting. Logs of the failed run:'
                Write-Host ''
                # Stop the restart loop first, then show only the failed run: Docker has usually
                # restarted the container by now, so a plain tail would end in the next boot's
                # first lines with the crash buried above them.
                docker compose stop server
                if ($restarts -gt $restarts0) {
                    $startedAt = docker inspect -f '{{.State.StartedAt}}' $cid 2>$null
                    docker logs --tail 200 --until $startedAt $cid
                } else {
                    docker logs --tail 200 $cid
                }
                Write-Host ''
                Die 'Server failed to start. Fix the issue above, then: docker compose up -d; docker compose exec server attach-cli'
            }
            $ready = $false
            try { docker compose exec -T server sh -c 'test -f /tmp/server-output.log' 2>$null; if ($LASTEXITCODE -eq 0) { $ready = $true } } catch { }
            if ($ready) { break }
            Start-Sleep 3; $waited += 3
        }

        Write-Host ''
        Write-Host 'Opening the CLI...'
        docker compose exec server attach-cli
    }
}
} finally {
    Pop-Location
}

} catch {
    Write-Host "Error: $($_.Exception.Message)" -ForegroundColor Red
}
