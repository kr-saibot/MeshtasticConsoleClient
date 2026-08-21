$ErrorActionPreference = "Stop"

# This is the canonical build destination. Do not change it without updating AGENTS.md.
$BuildRoot = "C:\Users\Tobias\Documents\ConsoleClientBuilds"
$WindowsTarget = Join-Path $BuildRoot "Windows"
$LinuxTarget = Join-Path $BuildRoot "Linux"

# Build into isolated staging folders. This prevents the Windows host from
# overwriting ConsoleClient dependencies with assemblies of another version.
$RepositoryRoot = Split-Path -Parent $PSScriptRoot
$StagingRoot = Join-Path $RepositoryRoot ".build-staging"
$WindowsStaging = Join-Path $StagingRoot "Windows"
$HostStaging = Join-Path $StagingRoot "WindowsHost"
$LinuxStaging = Join-Path $StagingRoot "Linux"

function Reset-StagingDirectory([string] $Path) {
    $resolvedRoot = [IO.Path]::GetFullPath($StagingRoot)
    $resolvedPath = [IO.Path]::GetFullPath($Path)
    if (-not $resolvedPath.StartsWith($resolvedRoot, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to clean a directory outside the staging root: $resolvedPath"
    }
    if (Test-Path -LiteralPath $resolvedPath) {
        Remove-Item -LiteralPath $resolvedPath -Recurse -Force
    }
    New-Item -ItemType Directory -Path $resolvedPath | Out-Null
}

function Copy-BuildFiles([string] $Source, [string] $Destination) {
    New-Item -ItemType Directory -Path $Destination -Force | Out-Null
    Copy-Item -Path (Join-Path $Source "*") -Destination $Destination -Recurse -Force
}

Push-Location $RepositoryRoot
try {
    Reset-StagingDirectory $WindowsStaging
    Reset-StagingDirectory $HostStaging
    Reset-StagingDirectory $LinuxStaging

    dotnet restore "src\ConsoleClient\ConsoleClient.csproj" -p:Configuration=Release
    if ($LASTEXITCODE -ne 0) { throw "Windows package restore failed." }
    dotnet build "src\ConsoleClient\ConsoleClient.csproj" --configuration Release --no-restore -p:OutputPath="$WindowsStaging\"
    if ($LASTEXITCODE -ne 0) { throw "Windows ConsoleClient build failed." }

    dotnet restore "src\ConsoleHost\ConsoleHost.csproj" -p:Configuration=Release
    if ($LASTEXITCODE -ne 0) { throw "Windows host package restore failed." }
    dotnet build "src\ConsoleHost\ConsoleHost.csproj" --configuration Release --no-restore -p:OutputPath="$HostStaging\"
    if ($LASTEXITCODE -ne 0) { throw "Windows host build failed." }

    dotnet restore "src\ConsoleClient\ConsoleClient.csproj" -p:Configuration=Linux
    if ($LASTEXITCODE -ne 0) { throw "Linux package restore failed." }
    dotnet build "src\ConsoleClient\ConsoleClient.csproj" --configuration Linux --no-restore -p:OutputPath="$LinuxStaging\"
    if ($LASTEXITCODE -ne 0) { throw "Linux ConsoleClient build failed." }

    # Copying with -Force replaces old program files but deliberately does not
    # delete runtime data already present in the destinations. In particular,
    # meshtastic-settings.xml, meshtastic-messages.db and Host\settings.ini survive.
    Copy-BuildFiles $WindowsStaging $WindowsTarget
    Copy-BuildFiles $HostStaging (Join-Path $WindowsTarget "Host")
    Copy-BuildFiles $LinuxStaging $LinuxTarget

    Write-Host ""
    Write-Host "Builds completed successfully:"
    Write-Host "  Windows: $WindowsTarget"
    Write-Host "  Linux:   $LinuxTarget"
    Write-Host "Existing settings and databases were preserved."
}
finally {
    Pop-Location
}
