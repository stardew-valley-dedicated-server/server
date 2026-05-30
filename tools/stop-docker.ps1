function Stop-DockerDesktop {
    <#
    .SYNOPSIS
        Forcibly stops Docker Desktop service and all related processes.

    .DESCRIPTION
        This function stops the Docker Desktop Service and forcibly terminates
        all Docker Desktop related processes.

    .EXAMPLE
        Stop-DockerDesktop
    #>
    [CmdletBinding()]
    param()

    # Stop the Docker Desktop Service
    $serviceName = 'com.docker.service'
    $service = Get-Service -Name $serviceName -ErrorAction SilentlyContinue

    if ($service) {
        if ($service.Status -eq 'Running') {
            Write-Verbose "Stopping service: $serviceName"
            Stop-Service -Name $serviceName -Force -ErrorAction SilentlyContinue
        }
        else {
            Write-Verbose "Service '$serviceName' is not running (Status: $($service.Status))"
        }
    }
    else {
        Write-Warning "Service '$serviceName' not found"
    }

    # Define the processes to kill
    $processNames = @(
        'Docker Desktop'
        'com.docker.build'
        'com.docker.service'
        'com.docker.backend'
    )

    # Forcibly stop each process
    foreach ($processName in $processNames) {
        $processes = Get-Process -Name $processName -ErrorAction SilentlyContinue

        if ($processes) {
            Write-Verbose "Stopping process(es): $processName (Count: $($processes.Count))"
            $processes | Stop-Process -Force -ErrorAction SilentlyContinue
        }
        else {
            Write-Verbose "No running process found: $processName"
        }
    }

    Write-Output "Docker Desktop has been forcibly stopped."
}
