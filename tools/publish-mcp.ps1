<#
.SYNOPSIS
    Publishes this repository's MCP server exe from a clean, merged main into the folder the consuming agents
    run it from, and writes a manifest naming the commit and every file's SHA-256.

.DESCRIPTION
    This is a DEPLOY, not a release into a drop folder: every seat whose .mcp.json names the exe runs the new
    build at its next session start. The script is the one path into that folder, for Matt and for the coding
    agent alike, and the coding agent runs it only when a Build Order says to publish; never as a side step of a
    merge. The order names the seats that run the exe, so their leaders can be told.

    It refuses, with the reason named and exit code 1, unless all of these hold:
      1. the checkout is on the branch named by -Branch (main);
      2. the working tree is clean (no modified, staged, deleted or untracked files: an untracked .cs would compile);
      3. HEAD equals origin/<branch> after a fetch, so an unmerged or unpushed commit can never be deployed;
      4. dotnet is on the path.

    It then cleans and publishes the project in Release into a staging folder under %TEMP% (the project's own
    publish properties decide single-file, runtime and self-contained), checks every required file is present,
    and swaps the staging folder into the destination:
      - a destination file that no process holds open is overwritten;
      - a destination file that a running session holds open (a stdio MCP server keeps its exe mapped for the
        life of the session) is RENAMED ASIDE to <name>.<utc-stamp>.previous and the new file copied in. Windows
        permits renaming a running executable, so the running session keeps its binary under the new name and
        every session started afterwards gets the new build. Nothing waits and nothing is killed;
      - files the publish did not produce are removed and named, so a stale file cannot run by mistake;
      - .previous files from earlier runs are deleted once nothing holds them; one still held is left and named.
    It writes publish-manifest.json beside the exe: repository, product version, commit, branch, UTC time,
    machine, each file's size and SHA-256, and which files were renamed aside. The same values are printed, so a
    hand-back can quote them.

    Pull-request checks already built and tested the merged commit; this script does not run the test suites.

.EXAMPLE
    powershell -NoProfile -ExecutionPolicy Bypass -File tools\publish-mcp.ps1

    The -ExecutionPolicy Bypass flag is required on a machine whose policy is Restricted; it applies to this one
    process and changes no setting.

.NOTES
    Windows PowerShell 5.1 compatible: no pipeline-chain operators, no ternary, no stderr redirection of native
    commands. The same script, with different defaults, lives in each repository that publishes an MCP exe under
    C:\Workspace\Apps; the folder name is the exe's base name. It is the publish-assemblies.ps1 body plus the
    rename-aside swap. "In use" is decided by trying to open the file for writing, never by listing processes:
    a process another session or elevation level started may hide its path from this one, and the lock is the
    fact that matters.
#>
[CmdletBinding()]
param(
    [string]$Project = "src\Roadbed.Net.Mcp\Roadbed.Net.Mcp.csproj",
    [string]$Destination = "C:\Workspace\Apps\Roadbed.Net.Mcp",
    [string]$Branch = "main",
    [string]$Include = "*",
    [string[]]$Keep = @(),
    [string]$Repository = "mattonweb/Roadbed.Net.Mcp",
    [string[]]$RequiredFiles = @("Roadbed.Net.Mcp.exe", "Roadbed.Net.Mcp.xml")
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot
$projectPath = Join-Path $repoRoot $Project
$manifestName = "publish-manifest.json"
$previousSuffix = ".previous"

function Stop-Refused {
    param([string]$Reason)
    Write-Host ""
    Write-Host "REFUSED: $Reason" -ForegroundColor Red
    exit 1
}

function Invoke-GitRead {
    # Runs a read-only git command and returns its stdout lines; refuses on a non-zero exit.
    param([string[]]$Arguments)
    $output = & git -C $repoRoot @Arguments
    if ($LASTEXITCODE -ne 0) {
        Stop-Refused "git $($Arguments -join ' ') exited with $LASTEXITCODE."
    }
    return $output
}

function Invoke-Checked {
    # Runs a native command whose output should stream to the console; refuses on a non-zero exit.
    param([string]$Label, [string]$Command, [string[]]$Arguments)
    Write-Host "> $Command $($Arguments -join ' ')" -ForegroundColor DarkGray
    & $Command @Arguments
    if ($LASTEXITCODE -ne 0) {
        Stop-Refused "$Label failed: $Command exited with $LASTEXITCODE."
    }
}

function Test-FileHeld {
    # True when something holds the file so that it cannot be overwritten: the probe asks for write access with
    # no sharing. A mapped executable or an open log both answer "held"; a missing file answers "not held".
    param([string]$Path)
    if (-not (Test-Path -LiteralPath $Path)) { return $false }
    try {
        $stream = [System.IO.File]::Open($Path, [System.IO.FileMode]::Open, [System.IO.FileAccess]::ReadWrite, [System.IO.FileShare]::None)
        $stream.Close()
        return $false
    }
    catch [System.IO.IOException] {
        return $true
    }
    catch [System.UnauthorizedAccessException] {
        return $true
    }
}

function Get-HoldersDescription {
    # Best effort, for the report only: the processes this session can see running from the folder.
    param([string]$Folder)
    $holders = @()
    foreach ($process in @(Get-Process -ErrorAction SilentlyContinue)) {
        $path = $null
        try { $path = $process.Path } catch { $path = $null }
        if ($null -ne $path -and $path.StartsWith($Folder + "\", [System.StringComparison]::OrdinalIgnoreCase)) {
            $holders += ("{0} (pid {1}, started {2:HH:mm:ss})" -f $process.ProcessName, $process.Id, $process.StartTime)
        }
    }
    if ($holders.Count -eq 0) { return "a process this session cannot see" }
    return ($holders -join "; ")
}

Write-Host "Repository: $repoRoot ($Repository)"
Write-Host "Project:    $Project"
Write-Host "Deploys to: $Destination"
Write-Host "Required:   $($RequiredFiles -join ', ')"
Write-Host ""

# ---------------------------------------------------------------- guards

if (-not (Test-Path -LiteralPath $projectPath)) {
    Stop-Refused "project file not found: $projectPath"
}
if ($RequiredFiles.Count -eq 0) {
    Stop-Refused "-RequiredFiles is empty; name the exe at least."
}

$dotnet = Get-Command dotnet -ErrorAction SilentlyContinue
if ($null -eq $dotnet) {
    Stop-Refused "dotnet is not on the path."
}

$currentBranch = (Invoke-GitRead @("rev-parse", "--abbrev-ref", "HEAD")) | Select-Object -First 1
if ($currentBranch -ne $Branch) {
    Stop-Refused "the checkout is on '$currentBranch'; a publish comes only from '$Branch'. Check out $Branch and pull first."
}

$dirty = @(Invoke-GitRead @("status", "--porcelain", "--untracked-files=all"))
if ($dirty.Count -gt 0) {
    Stop-Refused ("the working tree is not clean; commit, stash or remove these first:`n  " + ($dirty -join "`n  "))
}

Write-Host "> git fetch origin $Branch" -ForegroundColor DarkGray
& git -C $repoRoot fetch --quiet origin $Branch
if ($LASTEXITCODE -ne 0) {
    Stop-Refused "git fetch origin $Branch exited with $LASTEXITCODE; a publish needs the remote to compare against."
}

$headSha = (Invoke-GitRead @("rev-parse", "HEAD")) | Select-Object -First 1
$originSha = (Invoke-GitRead @("rev-parse", "origin/$Branch")) | Select-Object -First 1
if ($headSha -ne $originSha) {
    Stop-Refused "HEAD ($headSha) is not origin/$Branch ($originSha). Only a merged, pushed $Branch is published: pull, or push, then run again."
}

$headSummary = (Invoke-GitRead @("log", "-1", "--format=%h %ad %s", "--date=short")) | Select-Object -First 1
Write-Host "Publishing commit $headSummary"
Write-Host ""

# ---------------------------------------------------------------- build into staging

$staging = Join-Path $env:TEMP ("publish-mcp-" + [Guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory -Path $staging | Out-Null

try {
    Invoke-Checked -Label "clean" -Command "dotnet" -Arguments @("clean", $projectPath, "-c", "Release", "--nologo", "-v", "quiet")
    Invoke-Checked -Label "publish" -Command "dotnet" -Arguments @(
        "publish", $projectPath, "-c", "Release", "-o", $staging, "--nologo", "-v", "quiet",
        "-p:PublishDocumentationFile=true", "-p:PublishDocumentationFiles=true"
    )

    $missing = @()
    foreach ($required in $RequiredFiles) {
        if (-not (Test-Path -LiteralPath (Join-Path $staging $required))) {
            $missing += $required
        }
    }
    if ($missing.Count -gt 0) {
        Stop-Refused ("the publish produced no " + ($missing -join ", ") + " in $staging.")
    }

    # ------------------------------------------------------------ swap into the destination

    if (-not (Test-Path -LiteralPath $Destination)) {
        New-Item -ItemType Directory -Path $Destination | Out-Null
    }
    $destinationFull = (Resolve-Path -LiteralPath $Destination).Path

    # Earlier runs' renamed-aside files: delete the ones nothing holds any more.
    $previousKept = @()
    foreach ($old in @(Get-ChildItem -LiteralPath $Destination -File -Filter ("*" + $previousSuffix))) {
        if (Test-FileHeld -Path $old.FullName) {
            $previousKept += $old.Name
        }
        else {
            Remove-Item -LiteralPath $old.FullName -Force
        }
    }

    $produced = @(Get-ChildItem -LiteralPath $staging -File | Where-Object { $_.Name -like $Include })
    if ($produced.Count -eq 0) {
        Stop-Refused "no produced file matches -Include '$Include'."
    }

    $stamp = [DateTime]::UtcNow.ToString("yyyyMMddTHHmmssZ")
    $renamedAside = @()
    $producedNames = @{}
    foreach ($file in $produced) {
        $producedNames[$file.Name] = $true
        $target = Join-Path $Destination $file.Name
        if (Test-FileHeld -Path $target) {
            $asideName = $file.Name + "." + $stamp + $previousSuffix
            try {
                Move-Item -LiteralPath $target -Destination (Join-Path $Destination $asideName) -Force
            }
            catch {
                Stop-Refused ("{0} is held by {1} and could not be renamed aside: {2}" -f $file.Name, (Get-HoldersDescription -Folder $destinationFull), $_.Exception.Message)
            }
            $renamedAside += ("{0} -> {1} (held by {2})" -f $file.Name, $asideName, (Get-HoldersDescription -Folder $destinationFull))
        }
        Copy-Item -LiteralPath $file.FullName -Destination $target -Force
    }

    $removed = @()
    foreach ($existing in @(Get-ChildItem -LiteralPath $Destination -File)) {
        if ($existing.Name -eq $manifestName) { continue }
        if ($existing.Name.EndsWith($previousSuffix)) { continue }
        if ($Keep -contains $existing.Name) { continue }
        if (-not $producedNames.ContainsKey($existing.Name)) {
            if (Test-FileHeld -Path $existing.FullName) {
                $asideName = $existing.Name + "." + $stamp + $previousSuffix
                Move-Item -LiteralPath $existing.FullName -Destination (Join-Path $Destination $asideName) -Force
                $removed += ($existing.Name + " (held; renamed aside as " + $asideName + ")")
            }
            else {
                Remove-Item -LiteralPath $existing.FullName -Force
                $removed += $existing.Name
            }
        }
    }

    # ------------------------------------------------------------ manifest

    $firstRequired = Join-Path $Destination $RequiredFiles[0]
    $version = [System.Diagnostics.FileVersionInfo]::GetVersionInfo($firstRequired).ProductVersion
    $files = @()
    foreach ($file in @(Get-ChildItem -LiteralPath $Destination -File | Where-Object { $_.Name -ne $manifestName -and -not $_.Name.EndsWith($previousSuffix) } | Sort-Object Name)) {
        $hash = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
        $entry = [ordered]@{ name = $file.Name; bytes = $file.Length; sha256 = $hash }
        if ($file.Extension -eq ".exe" -or $file.Extension -eq ".dll") {
            $entry["version"] = [System.Diagnostics.FileVersionInfo]::GetVersionInfo($file.FullName).ProductVersion
        }
        $files += $entry
    }

    $manifest = [ordered]@{
        repository           = $Repository
        mode                 = "deploy"
        project              = $Project
        informationalVersion = $version
        commit               = $headSha
        branch               = $Branch
        publishedUtc         = [DateTime]::UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ")
        machine              = $env:COMPUTERNAME
        renamedAside         = $renamedAside
        previousStillHeld    = $previousKept
        removed              = $removed
        files                = $files
    }
    $json = $manifest | ConvertTo-Json -Depth 4
    [System.IO.File]::WriteAllText((Join-Path $Destination $manifestName), $json, (New-Object System.Text.UTF8Encoding $false))

    # ------------------------------------------------------------ report

    Write-Host ""
    Write-Host "PUBLISHED $Repository ($($files.Count) files, $($RequiredFiles[0]) $version) from $headSha to $Destination" -ForegroundColor Green
    Write-Host "Every seat whose .mcp.json names this exe runs it at its next session start." -ForegroundColor Green
    Write-Host ""
    foreach ($entry in $files) {
        Write-Host ("  {0,-48} {1,12:N0}  {2}" -f $entry.name, $entry.bytes, $entry.sha256)
    }
    if ($renamedAside.Count -gt 0) {
        Write-Host ""
        Write-Host "  renamed aside (a running session keeps the old build until it ends):" -ForegroundColor Yellow
        foreach ($line in $renamedAside) { Write-Host "    $line" -ForegroundColor Yellow }
    }
    if ($previousKept.Count -gt 0) {
        Write-Host ""
        Write-Host ("  still held from earlier runs, left in place: " + ($previousKept -join ", ")) -ForegroundColor Yellow
    }
    if ($removed.Count -gt 0) {
        Write-Host ""
        Write-Host ("  removed (not produced by this publish): " + ($removed -join ", ")) -ForegroundColor Yellow
    }
    Write-Host ""
    Write-Host "  manifest: $(Join-Path $Destination $manifestName)"
}
finally {
    if (Test-Path -LiteralPath $staging) {
        Remove-Item -LiteralPath $staging -Recurse -Force
    }
}

exit 0
