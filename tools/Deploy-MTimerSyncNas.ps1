[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateNotNullOrEmpty()]
    [string] $NasHost,

    [Parameter(Mandatory = $true)]
    [ValidateNotNullOrEmpty()]
    [string] $NasUser,

    [Parameter(Mandatory = $true)]
    [ValidateNotNullOrEmpty()]
    [string] $RemoteProjectPath,

    [ValidateNotNullOrEmpty()]
    [string] $Revision = "HEAD",

    [ValidateNotNullOrEmpty()]
    [string] $NasShare = "Container",

    [ValidateNotNullOrEmpty()]
    [string] $NasProjectName = "MTimer",

    [switch] $ValidateOnly
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$projectNames = @(
    "MTimer.Sync.Contracts",
    "MTimer.Sync.Api"
)
$relativeProjectPaths = @($projectNames)

function Invoke-ExternalCommand {
    param(
        [Parameter(Mandatory = $true)]
        [string] $Description,

        [Parameter(Mandatory = $true)]
        [scriptblock] $Command
    )

    & $Command
    if ($LASTEXITCODE -ne 0)
    {
        throw "$Description failed with exit code $LASTEXITCODE."
    }
}

function Invoke-NasCommand {
    param(
        [Parameter(Mandatory = $true)]
        [string] $Description,

        [Parameter(Mandatory = $true)]
        [string] $Command
    )

    Write-Host "`n$Description (SSH may request the NAS password)..." -ForegroundColor Cyan
    Invoke-ExternalCommand -Description $Description -Command {
        & $script:sshExecutable -t "$script:NasUser@$script:NasHost" $Command
    }
}

function Get-RelativeSourceFiles {
    param(
        [Parameter(Mandatory = $true)]
        [string] $BasePath
    )

    $normalizedBasePath = $BasePath.TrimEnd("\")
    return @(
        Get-ChildItem -LiteralPath $normalizedBasePath -Recurse -File |
            Where-Object { $_.FullName -notmatch "\\(bin|obj)\\" } |
            ForEach-Object { $_.FullName.Substring($normalizedBasePath.Length + 1) }
    )
}

function Test-SourceHashes {
    param(
        [Parameter(Mandatory = $true)]
        [string] $StagingRoot,

        [Parameter(Mandatory = $true)]
        [string] $NasRoot
    )

    $mismatches = [System.Collections.Generic.List[string]]::new()
    foreach ($projectName in $script:projectNames)
    {
        $stagingProject = Join-Path $StagingRoot $projectName
        $nasProject = Join-Path $NasRoot $projectName
        foreach ($file in Get-ChildItem -LiteralPath $stagingProject -Recurse -File)
        {
            $relativePath = $file.FullName.Substring($stagingProject.Length + 1)
            $nasFile = Join-Path $nasProject $relativePath
            if (-not (Test-Path -LiteralPath $nasFile -PathType Leaf))
            {
                $mismatches.Add("Missing: $projectName/$relativePath")
                continue
            }

            $sourceHash = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash
            $nasHash = (Get-FileHash -LiteralPath $nasFile -Algorithm SHA256).Hash
            if ($sourceHash -ne $nasHash)
            {
                $mismatches.Add("Content mismatch: $projectName/$relativePath")
            }
        }
    }

    if ($mismatches.Count -gt 0)
    {
        throw "NAS source verification failed:`n$($mismatches -join "`n")"
    }
}

function Wait-ForSyncApi {
    param(
        [Parameter(Mandatory = $true)]
        [uri] $HealthUri,

        [int] $Attempts = 30,

        [int] $DelaySeconds = 2
    )

    for ($attempt = 1; $attempt -le $Attempts; $attempt++)
    {
        try
        {
            $health = Invoke-RestMethod -Uri $HealthUri -TimeoutSec 5
            if ($health.status -eq "ok")
            {
                return $health
            }
        }
        catch
        {
            if ($attempt -eq $Attempts)
            {
                throw
            }
        }

        Start-Sleep -Seconds $DelaySeconds
    }

    throw "The sync API did not become healthy in time: $HealthUri"
}

$repoRoot = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot "..")).Path
$gitExecutable = (Get-Command git.exe -ErrorAction Stop).Source
$sshExecutable = (Get-Command ssh.exe -ErrorAction Stop).Source
$nasRoot = "\\$NasHost\$NasShare\$NasProjectName"
$resolvedNasRoot = (Get-Item -LiteralPath $nasRoot -ErrorAction Stop).FullName.TrimEnd("\")
if (-not [string]::Equals($resolvedNasRoot, $nasRoot.TrimEnd("\"), [StringComparison]::OrdinalIgnoreCase))
{
    throw "NAS root validation failed: $resolvedNasRoot"
}

$requiredNasPaths = @(
    ".dockerignore",
    "docker-compose.yml",
    "data\mtimer-sync\sync.db",
    "MTimer.Sync.Contracts\MTimer.Sync.Contracts.csproj",
    "MTimer.Sync.Api\MTimer.Sync.Api.csproj"
)
foreach ($relativePath in $requiredNasPaths)
{
    $requiredPath = Join-Path $resolvedNasRoot $relativePath
    if (-not (Test-Path -LiteralPath $requiredPath))
    {
        throw "Required NAS project marker is missing: $requiredPath"
    }
}

if ($RemoteProjectPath.Contains("'"))
{
    throw "RemoteProjectPath cannot contain a single quote."
}

$commitOutput = & $gitExecutable -C $repoRoot rev-parse --verify "${Revision}^{commit}"
if ($LASTEXITCODE -ne 0)
{
    throw "Cannot resolve Git revision: $Revision"
}
$commit = (($commitOutput | Out-String).Trim())

$workingTreeStatus = & $gitExecutable -C $repoRoot status --short
if ($LASTEXITCODE -ne 0)
{
    throw "Cannot read the Git working tree status."
}
if ($workingTreeStatus)
{
    Write-Warning "The working tree has uncommitted changes. Deployment uses commit $commit only."
}

$stamp = Get-Date -Format "yyyyMMdd-HHmmss"
$tempBase = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd("\") + "\"
$tempRoot = [IO.Path]::GetFullPath((Join-Path $tempBase "MTimer.NasDeploy.$stamp"))
if (-not $tempRoot.StartsWith($tempBase, [StringComparison]::OrdinalIgnoreCase))
{
    throw "The staging directory is outside the system temp directory: $tempRoot"
}

$archivePath = Join-Path $tempRoot "server-source.zip"
$expandedRoot = Join-Path $tempRoot "expanded"
$nasSourceRoot = $resolvedNasRoot
$deploymentSucceeded = $false

try
{
    New-Item -ItemType Directory -Path $tempRoot -Force | Out-Null
    Invoke-ExternalCommand -Description "Create Git deployment archive" -Command {
        & $gitExecutable -C $repoRoot archive --format=zip "--output=$archivePath" $commit -- @relativeProjectPaths
    }
    Expand-Archive -LiteralPath $archivePath -DestinationPath $expandedRoot

    foreach ($projectName in $projectNames)
    {
        $stagingProject = Join-Path $expandedRoot $projectName
        if (-not (Test-Path -LiteralPath $stagingProject -PathType Container))
        {
            throw "The Git archive is missing a project directory: $stagingProject"
        }

        $nasProject = Join-Path $nasSourceRoot $projectName
        $stagingFiles = Get-RelativeSourceFiles -BasePath $stagingProject
        $nasFiles = Get-RelativeSourceFiles -BasePath $nasProject
        $nasOnlyFiles = @(
            Compare-Object -ReferenceObject $stagingFiles -DifferenceObject $nasFiles -PassThru |
                Where-Object { $_.SideIndicator -eq "=>" }
        )
        if ($nasOnlyFiles.Count -gt 0)
        {
            throw "NAS project $projectName contains source files that are not in the selected commit. The script will not delete them automatically:`n$($nasOnlyFiles -join "`n")"
        }
    }

    $protocolFile = Join-Path $expandedRoot "MTimer.Sync.Contracts\SyncContracts.cs"
    $protocolText = [IO.File]::ReadAllText($protocolFile, [Text.UTF8Encoding]::UTF8)
    $protocolMatch = [regex]::Match($protocolText, "CurrentVersion\s*=\s*(\d+)")
    if (-not $protocolMatch.Success)
    {
        throw "Cannot read the sync protocol version from the Git archive."
    }
    $expectedProtocolVersion = [int] $protocolMatch.Groups[1].Value

    if ($ValidateOnly)
    {
        Write-Host "Validation passed. Commit $commit is deployable; expected sync protocol: v$expectedProtocolVersion." -ForegroundColor Green
        $deploymentSucceeded = $true
        return
    }

    $quotedRemoteProjectPath = "'$RemoteProjectPath'"
    $remotePreamble = 'export HOME=/tmp; export DOCKER_CONFIG="/tmp/mtimer-docker-config-$(id -u)"; mkdir -p "$DOCKER_CONFIG"; CS_ROOT="$(/sbin/getcfg container-station Install_Path -f /etc/config/qpkg.conf)"; COMPOSE=""; for CANDIDATE in "$CS_ROOT/usr/local/lib/docker/cli-plugins/docker-compose" "$CS_ROOT/lib/docker/cli-plugins/docker-compose" "$CS_ROOT/bin/docker-compose"; do if [ -x "$CANDIDATE" ]; then COMPOSE="$CANDIDATE"; break; fi; done; if [ -z "$COMPOSE" ]; then echo "Container Station Compose plugin not found." >&2; exit 127; fi'

    $backupRoot = "\\$NasHost\$NasShare\$NasProjectName-deploy-backups\$stamp"
    New-Item -ItemType Directory -Path $backupRoot -Force | Out-Null
    Copy-Item -LiteralPath (Join-Path $resolvedNasRoot ".dockerignore") -Destination (Join-Path $backupRoot ".dockerignore")
    Copy-Item -LiteralPath (Join-Path $resolvedNasRoot "docker-compose.yml") -Destination (Join-Path $backupRoot "docker-compose.yml")
    foreach ($projectName in $projectNames)
    {
        Copy-Item -LiteralPath (Join-Path $nasSourceRoot $projectName) -Destination $backupRoot -Recurse
    }
    Write-Host "Prepared backup: $backupRoot"

    Invoke-NasCommand -Description "Stop the MTimer sync container" -Command "$remotePreamble; cd $quotedRemoteProjectPath && `"`$COMPOSE`" stop mtimer-sync"
    try
    {
        Copy-Item -LiteralPath (Join-Path $resolvedNasRoot "data\mtimer-sync\sync.db") -Destination (Join-Path $backupRoot "sync.db")
    }
    catch
    {
        throw "Backing up sync.db failed after the container stopped. Start the existing container before retrying. Backup directory: $backupRoot.$([Environment]::NewLine)$($_.Exception.Message)"
    }

    try
    {
        foreach ($projectName in $projectNames)
        {
            $stagingProject = Join-Path $expandedRoot $projectName
            $nasProject = Join-Path $nasSourceRoot $projectName
            Get-ChildItem -LiteralPath $stagingProject -Force |
                Copy-Item -Destination $nasProject -Recurse -Force
        }
        Test-SourceHashes -StagingRoot $expandedRoot -NasRoot $nasSourceRoot
    }
    catch
    {
        throw "Copying or verifying NAS source failed. The container remains stopped; the database and previous source are in $backupRoot.$([Environment]::NewLine)$($_.Exception.Message)"
    }

    Invoke-NasCommand -Description "Rebuild and start the MTimer sync container" -Command "$remotePreamble; cd $quotedRemoteProjectPath && `"`$COMPOSE`" up -d --build mtimer-sync"

    $healthUri = [uri] "http://$NasHost`:5124/health"
    $health = Wait-ForSyncApi -HealthUri $healthUri
    if ([int] $health.protocolVersion -ne $expectedProtocolVersion)
    {
        throw "The health endpoint reports protocol v$($health.protocolVersion), expected v$expectedProtocolVersion."
    }

    $pushBody = @{
        protocolVersion = $expectedProtocolVersion
        deviceId = "mtimer-deploy-verifier"
        deviceName = "V"
        changes = @()
    } | ConvertTo-Json -Depth 5
    $pushResponse = Invoke-RestMethod -Uri "http://$NasHost`:5124/sync/push" -Method Post -ContentType "application/json" -Body $pushBody -TimeoutSec 10
    if ([int] $pushResponse.protocolVersion -ne $expectedProtocolVersion)
    {
        throw "The push endpoint reports protocol v$($pushResponse.protocolVersion), expected v$expectedProtocolVersion."
    }

    $pullResponse = Invoke-RestMethod -Uri "http://$NasHost`:5124/sync/pull?after=0&protocolVersion=$expectedProtocolVersion" -TimeoutSec 10
    if ([int] $pullResponse.protocolVersion -ne $expectedProtocolVersion)
    {
        throw "The pull endpoint reports protocol v$($pullResponse.protocolVersion), expected v$expectedProtocolVersion."
    }

    Write-Host "`nDeployment completed." -ForegroundColor Green
    Write-Host "Commit: $commit"
    Write-Host "Service: $($health.service) / $($health.status)"
    Write-Host "Protocol: v$($pushResponse.protocolVersion)"
    Write-Host "Cursor: $($pullResponse.serverCursor)"
    Write-Host "Backup: $backupRoot"
    $deploymentSucceeded = $true
}
finally
{
    if ($deploymentSucceeded -and (Test-Path -LiteralPath $tempRoot))
    {
        $resolvedTempRoot = [IO.Path]::GetFullPath((Get-Item -LiteralPath $tempRoot).FullName)
        if ($resolvedTempRoot.StartsWith($tempBase, [StringComparison]::OrdinalIgnoreCase))
        {
            Remove-Item -LiteralPath $resolvedTempRoot -Recurse -Force
        }
    }
    elseif (Test-Path -LiteralPath $tempRoot)
    {
        Write-Warning "Deployment did not complete. Staging files remain at: $tempRoot"
    }
}
