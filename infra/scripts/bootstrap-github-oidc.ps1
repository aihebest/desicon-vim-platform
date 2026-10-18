<#
.SYNOPSIS
    One-time per environment: creates the two identities GitHub Actions uses
    to deploy VIM, trusted through OpenID Connect (no secrets stored anywhere).

.DESCRIPTION
    GitHub Actions needs to sign in to Azure to run Terraform and deploy code.
    The old way is a client secret stored in GitHub -- a password that never
    expires on its own and works from anywhere. OIDC replaces it: GitHub
    issues a short-lived token saying "this is workflow X in repo Y, running
    for environment Z", and Entra exchanges it for an Azure token only if a
    "federated credential" on the identity matches exactly that repo and
    environment. Nothing reusable is ever stored.

    Two identities per environment, kept separate on purpose:

    1. sp-vim-deploy-<env>   -- runs Terraform and deploys the apps.
         Azure roles (subscription scope, because Terraform creates the
         resource group itself):
           - Contributor                                 create/update resources
           - Role Based Access Control Administrator     create the app role
             assignments in modules/role-assignments -- CONSTRAINED by a
             condition to only the seven data-plane roles this platform uses,
             so it cannot make anyone Owner.
           - Storage Blob Data Contributor on the Terraform state account.
         Trusted for GitHub environments "<env>-plan" and "<env>".

    2. sp-vim-migrations-<env> -- runs EF Core database migrations only.
         NO Azure roles at all. Its only power is the db_ddladmin database
         role granted by infra/scripts/create-sql-users.sql. Trusted for
         GitHub environment "<env>" only.

    Safe to re-run.

.PARAMETER Environment
    dev, uat or prod.

.PARAMETER GitHubRepository
    owner/repo, e.g. aihebest/desicon-vim-platform.

.PARAMETER StateStorageAccountName
    The Terraform state account from bootstrap-state.ps1.

.PARAMETER StateResourceGroupName
    Its resource group (default rg-vim-tfstate).

.EXAMPLE
    az login
    az account set --subscription "<subscription for this environment>"
    .\bootstrap-github-oidc.ps1 -Environment dev -GitHubRepository aihebest/desicon-vim-platform `
        -StateStorageAccountName stvimtfstatea1b2
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [ValidateSet('dev', 'uat', 'prod')] [string] $Environment,
    [Parameter(Mandatory)] [ValidatePattern('^[^/]+/[^/]+$')] [string] $GitHubRepository,
    [Parameter(Mandatory)] [string] $StateStorageAccountName,
    [string] $StateResourceGroupName = 'rg-vim-tfstate'
)

$ErrorActionPreference = 'Stop'
$subscriptionId = az account show --query id -o tsv
$tenantId = az account show --query tenantId -o tsv
$issuer = 'https://token.actions.githubusercontent.com'

function Get-OrCreateApp([string] $name) {
    $appId = az ad app list --display-name $name --query "[0].appId" -o tsv
    if (-not $appId) {
        Write-Host "  Creating app registration $name"
        $appId = az ad app create --display-name $name --sign-in-audience AzureADMyOrg --query appId -o tsv
    }
    $spId = az ad sp list --filter "appId eq '$appId'" --query "[0].id" -o tsv
    if (-not $spId) { $spId = az ad sp create --id $appId --query id -o tsv }
    return @{ AppId = $appId; SpId = $spId }
}

function Add-FederatedCredential([string] $appId, [string] $credName, [string] $subject) {
    $exists = az ad app federated-credential list --id $appId --query "[?name=='$credName'] | length(@)" -o tsv
    if ($exists -ne '0') { Write-Host "  Federated credential $credName exists"; return }
    $file = Join-Path ([IO.Path]::GetTempPath()) "fic-$credName.json"
    $json = @{
        name        = $credName
        issuer      = $issuer
        subject     = $subject
        description = "GitHub Actions: $subject"
        audiences   = @('api://AzureADTokenExchange')
    } | ConvertTo-Json
    # WriteAllText: UTF-8 without a BOM (Set-Content -Encoding utf8 adds one in 5.1).
    [IO.File]::WriteAllText($file, $json)
    az ad app federated-credential create --id $appId --parameters "@$file" | Out-Null
    Remove-Item $file -ErrorAction SilentlyContinue
    Write-Host "  Trusts: $subject"
}

function Add-RoleIfMissing([string] $spId, [string] $role, [string] $scope, [string] $condition = $null) {
    # No parentheses in --query: az is az.cmd on Windows and cmd.exe mis-parses them.
    $has = az role assignment list --assignee $spId --scope $scope --role $role --query "[].id" -o tsv
    if ($has) { Write-Host "  Has '$role'"; return }
    $azArgs = @('role', 'assignment', 'create', '--assignee-object-id', $spId,
        '--assignee-principal-type', 'ServicePrincipal', '--role', $role, '--scope', $scope)
    if ($condition) { $azArgs += @('--condition', $condition, '--condition-version', '2.0') }
    az @azArgs | Out-Null
    Write-Host "  Granted '$role'"
}

# GitHub may present the repository in its newer, rename-proof form
# "repo:owner@<ownerId>/name@<repoId>:environment:<env>" instead of
# "repo:owner/name:environment:<env>". Entra matches the subject exactly, so
# trust both. The ids come from the GitHub CLI; without it only the classic
# form is added (add the other later by re-running with gh signed in).
$repoIdForm = $null
if (Get-Command gh -ErrorAction SilentlyContinue) {
    # One field per call: no quotes inside --jq (Windows PowerShell 5.1 strips them).
    $ghOwner   = gh api "repos/$GitHubRepository" --jq .owner.login 2>$null
    $ghOwnerId = gh api "repos/$GitHubRepository" --jq .owner.id 2>$null
    $ghName    = gh api "repos/$GitHubRepository" --jq .name 2>$null
    $ghRepoId  = gh api "repos/$GitHubRepository" --jq .id 2>$null
    if ($ghOwner -and $ghOwnerId -and $ghName -and $ghRepoId) {
        $repoIdForm = "$ghOwner@$ghOwnerId/$ghName@$ghRepoId"
    }
}
if (-not $repoIdForm) { Write-Warning "GitHub CLI not available or not signed in: adding only the classic subject format." }

function Add-GitHubTrust([string] $appId, [string] $credName, [string] $githubEnvironment) {
    Add-FederatedCredential $appId $credName "repo:${GitHubRepository}:environment:$githubEnvironment"
    if ($repoIdForm) {
        Add-FederatedCredential $appId "$credName-ids" "repo:${repoIdForm}:environment:$githubEnvironment"
    }
}

# -- 1. Deploy identity ------------------------------------------------------
Write-Host "1/2 Deploy identity" -ForegroundColor Cyan
$deploy = Get-OrCreateApp "sp-vim-deploy-$Environment"
Add-GitHubTrust $deploy.AppId "github-$Environment-plan" "$Environment-plan"
Add-GitHubTrust $deploy.AppId "github-$Environment" $Environment

$subScope = "/subscriptions/$subscriptionId"
Add-RoleIfMissing $deploy.SpId 'Contributor' $subScope

# The only roles Terraform assigns (modules/role-assignments and
# modules/keyvault). Built-in role definition IDs -- they are the same in
# every tenant. If a module starts assigning a new role, add its ID here or
# the apply fails with AuthorizationFailed (which is the point).
$allowedRoleIds = @(
    'ba92f5b4-2d11-453d-a403-e96b0029c9fe', # Storage Blob Data Contributor
    'b7e6dc6d-f1e8-4753-8033-0f276bb0955b', # Storage Blob Data Owner
    '974c5e8b-45b9-4653-ba55-5f855dd0fb88', # Storage Queue Data Contributor
    '0a9a7e1f-b9d0-4cc4-a60d-0319b160aaa3', # Storage Table Data Contributor
    'a97b65f3-24c7-4388-baec-2e87135dc908', # Cognitive Services User
    '4633458b-17de-408a-b874-0445c86b69e6', # Key Vault Secrets User
    'b86a8fe4-44ce-4948-aee5-eccb2c155cd7'  # Key Vault Secrets Officer
) -join ', '
$condition = "((!(ActionMatches{'Microsoft.Authorization/roleAssignments/write'})) OR " +
    "(@Request[Microsoft.Authorization/roleAssignments:RoleDefinitionId] ForAnyOfAnyValues:GuidEquals {$allowedRoleIds})) AND " +
    "((!(ActionMatches{'Microsoft.Authorization/roleAssignments/delete'})) OR " +
    "(@Resource[Microsoft.Authorization/roleAssignments:RoleDefinitionId] ForAnyOfAnyValues:GuidEquals {$allowedRoleIds}))"
Add-RoleIfMissing $deploy.SpId 'Role Based Access Control Administrator' $subScope $condition

$stateScope = az storage account show -n $StateStorageAccountName -g $StateResourceGroupName --query id -o tsv
Add-RoleIfMissing $deploy.SpId 'Storage Blob Data Contributor' $stateScope

# -- 2. Migration identity ------------------------------------------------------
Write-Host "2/2 Migration identity" -ForegroundColor Cyan
$migrations = Get-OrCreateApp "sp-vim-migrations-$Environment"
Add-GitHubTrust $migrations.AppId "github-$Environment" $Environment
Write-Host "  (No Azure roles -- database rights come from create-sql-users.sql)"

Write-Host ""
Write-Host "Done. Set these as VARIABLES (not secrets -- none of them is sensitive)" -ForegroundColor Green
Write-Host "on BOTH GitHub environments '$Environment-plan' and '$Environment':"
Write-Host "  AZURE_TENANT_ID            = $tenantId"
Write-Host "  AZURE_SUBSCRIPTION_ID      = $subscriptionId"
Write-Host "  AZURE_CLIENT_ID            = $($deploy.AppId)"
Write-Host "and on '$Environment' only:"
Write-Host "  AZURE_MIGRATION_CLIENT_ID  = $($migrations.AppId)"
Write-Host ""
Write-Host "Use 'sp-vim-migrations-$Environment' as MigrationIdentityName when running create-sql-users.sql."
