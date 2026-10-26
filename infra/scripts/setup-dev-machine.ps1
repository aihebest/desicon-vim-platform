<#
.SYNOPSIS
    Prepares a Windows PC for working on the VIM platform: installs the tools, VS Code
    extensions and the repository (or updates an existing copy), then builds and tests.

.DESCRIPTION
    Safe to run more than once: it skips anything already installed.
    An existing copy of the repository is updated without losing work: local edits are
    put aside with "git stash" (get them back with "git stash pop"), never deleted.

    Installs (via winget): Git, PowerShell 7, .NET 10 SDK, Azure CLI, GitHub CLI,
    Terraform, sqlcmd, Azure Functions Core Tools, and optionally Docker Desktop
    (only needed to run the apps locally against SQL Server/Azurite containers).

.EXAMPLE
    # From any PowerShell window (no admin needed for most tools; winget may ask):
    Set-ExecutionPolicy -Scope Process Bypass -Force
    .\setup-dev-machine.ps1                      # repo in C:\Dev\desicon-vim-platform
    .\setup-dev-machine.ps1 -IncludeDocker       # also Docker Desktop
#>
[CmdletBinding()]
param(
    [string] $RepoPath = 'C:\Dev\desicon-vim-platform',
    [string] $RepoUrl = 'https://github.com/aihebest/desicon-vim-platform.git',
    [switch] $IncludeDocker,
    [switch] $SkipBuild
)

$ErrorActionPreference = 'Stop'

function Step([string] $text) { Write-Host "`n== $text" -ForegroundColor Cyan }

function Update-SessionPath {
    $machine = [Environment]::GetEnvironmentVariable('Path', 'Machine')
    $user = [Environment]::GetEnvironmentVariable('Path', 'User')
    $env:Path = "$machine;$user"
}

function Install-Tool([string] $command, [string] $wingetId, [string] $label) {
    if (Get-Command $command -ErrorAction SilentlyContinue) {
        Write-Host "  OK  $label" -ForegroundColor Green
        return
    }
    Write-Host "  ... installing $label ($wingetId)"
    winget install --id $wingetId -e --silent --accept-source-agreements --accept-package-agreements | Out-Host
    Update-SessionPath
    if (Get-Command $command -ErrorAction SilentlyContinue) {
        Write-Host "  OK  $label installed" -ForegroundColor Green
    } else {
        Write-Warning "$label installed but '$command' is not on PATH yet. Close and reopen PowerShell / VS Code, then run this script again."
    }
}

# -- 1. Tools ------------------------------------------------------------------------
Step '1/5 Tools'
if (-not (Get-Command winget -ErrorAction SilentlyContinue)) {
    throw "winget is not available. Install 'App Installer' from the Microsoft Store, then run this script again."
}
Install-Tool git        'Git.Git'                              'Git'
Install-Tool pwsh       'Microsoft.PowerShell'                 'PowerShell 7'
Install-Tool az         'Microsoft.AzureCLI'                   'Azure CLI'
Install-Tool gh         'GitHub.cli'                           'GitHub CLI'
Install-Tool terraform  'Hashicorp.Terraform'                  'Terraform'
Install-Tool sqlcmd     'Microsoft.Sqlcmd'                     'sqlcmd'
Install-Tool func       'Microsoft.Azure.FunctionsCoreTools'   'Azure Functions Core Tools'
Install-Tool code       'Microsoft.VisualStudioCode'           'VS Code'
if ($IncludeDocker) { Install-Tool docker 'Docker.DockerDesktop' 'Docker Desktop' }

# .NET 10 SDK: "dotnet" may exist with only older SDKs, so check the SDK list.
$hasNet10 = (Get-Command dotnet -ErrorAction SilentlyContinue) -and ((dotnet --list-sdks) -match '^10\.')
if ($hasNet10) {
    Write-Host '  OK  .NET 10 SDK' -ForegroundColor Green
} else {
    Write-Host '  ... installing .NET 10 SDK'
    winget install --id Microsoft.DotNet.SDK.10 -e --silent --accept-source-agreements --accept-package-agreements | Out-Host
    Update-SessionPath
}

# EF Core tool (migrations).
if (Get-Command dotnet -ErrorAction SilentlyContinue) {
    $efInstalled = (dotnet tool list --global) -match 'dotnet-ef'
    if ($efInstalled) { dotnet tool update --global dotnet-ef --version '10.0.*' | Out-Null }
    else { dotnet tool install --global dotnet-ef --version '10.0.*' | Out-Null }
    Write-Host '  OK  dotnet-ef' -ForegroundColor Green
}

# -- 2. VS Code extensions -------------------------------------------------------
Step '2/5 VS Code extensions'
if (Get-Command code -ErrorAction SilentlyContinue) {
    $wanted = @(
        'ms-dotnettools.csdevkit',               # C# Dev Kit (build, test, debug)
        'ms-dotnettools.csharp',
        'ms-azuretools.vscode-azurefunctions',   # Functions app
        'ms-azuretools.vscode-azureresourcegroups',
        'hashicorp.terraform',                   # Terraform syntax and formatting
        'ms-vscode.powershell',                  # the infra\scripts
        'github.vscode-pull-request-github',     # pull requests from VS Code
        'github.vscode-github-actions',          # Deploy / CI workflow status
        'ms-mssql.mssql',                        # query Azure SQL
        'editorconfig.editorconfig'
    )
    if ($IncludeDocker) { $wanted += 'ms-azuretools.vscode-docker' }
    $have = code --list-extensions
    foreach ($ext in $wanted) {
        if ($have -contains $ext) { Write-Host "  OK  $ext" -ForegroundColor Green }
        else { code --install-extension $ext --force | Out-Null; Write-Host "  +   $ext" }
    }
} else {
    Write-Warning "VS Code's 'code' command is not on PATH. Reopen PowerShell after installing VS Code and run again."
}

# -- 3. Repository -----------------------------------------------------------------
Step '3/5 Repository'
if (Test-Path (Join-Path $RepoPath '.git')) {
    Push-Location $RepoPath
    try {
        git fetch origin --prune | Out-Host
        $dirty = git status --porcelain
        if ($dirty) {
            $stamp = Get-Date -Format 'yyyy-MM-dd HH:mm'
            git stash push --include-untracked -m "setup-dev-machine $stamp" | Out-Host
            Write-Host "  Local changes were put aside (git stash). Restore them with: git stash pop" -ForegroundColor Yellow
        }
        $branch = git rev-parse --abbrev-ref HEAD
        if ($branch -ne 'main') {
            Write-Host "  Was on branch '$branch'; switching to main (the branch is kept)."
            git checkout main | Out-Host
        }
        git pull --ff-only origin main | Out-Host
        if ($LASTEXITCODE -ne 0) {
            Write-Warning "main here has commits that are not on GitHub, so it could not be fast-forwarded. Nothing was changed; ask for help before forcing anything."
        }
        Write-Host "  Now at: $(git log --oneline -1)" -ForegroundColor Green
    } finally { Pop-Location }
} else {
    New-Item -ItemType Directory -Force -Path (Split-Path $RepoPath) | Out-Null
    git clone $RepoUrl $RepoPath | Out-Host
    Write-Host "  Cloned to $RepoPath" -ForegroundColor Green
}

# -- 4. Build and test -------------------------------------------------------------
Step '4/5 Build and test'
if ($SkipBuild) {
    Write-Host '  Skipped (-SkipBuild).'
} elseif (Get-Command dotnet -ErrorAction SilentlyContinue) {
    Push-Location $RepoPath
    try {
        dotnet restore Desicon.Vim.sln | Out-Host
        dotnet build Desicon.Vim.sln -c Release --no-restore | Out-Host
        if ($LASTEXITCODE -eq 0) {
            dotnet test Desicon.Vim.sln -c Release --no-build | Out-Host
        }
    } finally { Pop-Location }
} else {
    Write-Warning 'dotnet is not on PATH yet. Reopen PowerShell and run again to build.'
}

# -- 5. Sign-ins (interactive; only if not already signed in) ------------------------
Step '5/5 Sign-ins'
$azOk = $false
try { $null = az account show 2>$null; $azOk = ($LASTEXITCODE -eq 0) } catch { $azOk = $false }
Write-Host ("  Azure CLI : " + ($(if ($azOk) { 'signed in' } else { 'NOT signed in  ->  az login --tenant 77e479f1-fefd-4238-a1a9-6b1f692f20b8' })))
$ghOk = $false
try { gh auth status 2>$null | Out-Null; $ghOk = ($LASTEXITCODE -eq 0) } catch { $ghOk = $false }
Write-Host ("  GitHub CLI: " + ($(if ($ghOk) { 'signed in' } else { 'NOT signed in  ->  gh auth login' })))

Write-Host "`nDone. Open the repo with:  code `"$RepoPath`"" -ForegroundColor Green
