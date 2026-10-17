<#
.SYNOPSIS
    One-time: creates the storage account that holds Terraform state for all
    three VIM environments.

.DESCRIPTION
    Terraform records what it has built in a "state file". Kept on a laptop,
    it gets lost or diverges between people; kept in an Azure storage account
    it is shared, versioned and locked while someone is running apply.

    Terraform cannot create the account that stores its own state (it needs
    the state to exist before it can run), so this script does it once.

    The account follows the same rules as the platform's own storage:
    no shared keys (Entra sign-in only), TLS 1.2, no public blob access,
    blob versioning and soft delete so a damaged state file can be recovered.

    The signed-in user is given "Storage Blob Data Contributor" so that
    `terraform init` works from this PC. The CI identities get the same role
    from bootstrap-github-oidc.ps1.

    Safe to re-run.

.PARAMETER Location
    Region for the state account. Can be the same as the platform's.

.PARAMETER ResourceGroupName
    Resource group for state. Kept separate from the environments' groups so
    destroying an environment never touches its state.

.PARAMETER StorageAccountName
    Globally unique, 3-24 lowercase letters/digits. Default adds a random suffix.

.EXAMPLE
    az login
    az account set --subscription "<subscription name or id>"
    .\bootstrap-state.ps1
#>
[CmdletBinding()]
param(
    [string] $Location = 'westeurope',
    [string] $ResourceGroupName = 'rg-vim-tfstate',
    [ValidatePattern('^[a-z0-9]{3,24}$')]
    [string] $StorageAccountName = ('stvimtfstate' + -join ((97..122) + (48..57) | Get-Random -Count 4 | ForEach-Object { [char]$_ }))
)

$ErrorActionPreference = 'Stop'
$container = 'tfstate'

$subscriptionId = az account show --query id -o tsv
Write-Host "Subscription: $(az account show --query name -o tsv) ($subscriptionId)" -ForegroundColor Cyan

# Re-use an existing state account in this resource group if there is one, so
# a re-run does not create a second account with a different random name.
$existing = az storage account list -g $ResourceGroupName --query "[?starts_with(name, 'stvimtfstate')].name | [0]" -o tsv 2>$null
if ($existing) {
    Write-Host "Found existing state account $existing; re-using it."
    $StorageAccountName = $existing
}

Write-Host "1/5 Resource group $ResourceGroupName..."
az group create -n $ResourceGroupName -l $Location `
    --tags application=vim owner=ICT business_owner=Finance managed_by=bootstrap-script | Out-Null

Write-Host "2/5 Storage account $StorageAccountName..."
az storage account create -n $StorageAccountName -g $ResourceGroupName -l $Location `
    --sku Standard_ZRS --kind StorageV2 `
    --min-tls-version TLS1_2 --https-only true `
    --allow-blob-public-access false `
    --allow-shared-key-access false `
    --tags application=vim owner=ICT business_owner=Finance managed_by=bootstrap-script | Out-Null

Write-Host "3/5 Versioning and soft delete (recover a damaged state file)..."
az storage account blob-service-properties update -n $StorageAccountName -g $ResourceGroupName `
    --enable-versioning true `
    --enable-delete-retention true --delete-retention-days 30 `
    --enable-container-delete-retention true --container-delete-retention-days 30 | Out-Null

Write-Host "4/5 Granting you 'Storage Blob Data Contributor'..."
$me = az ad signed-in-user show --query id -o tsv
$scope = az storage account show -n $StorageAccountName -g $ResourceGroupName --query id -o tsv
$hasRole = az role assignment list --assignee $me --scope $scope --role "Storage Blob Data Contributor" --query "length(@)" -o tsv
if ($hasRole -eq '0') {
    az role assignment create --assignee-object-id $me --assignee-principal-type User `
        --role "Storage Blob Data Contributor" --scope $scope | Out-Null
}

Write-Host "5/5 Container '$container' (waiting for the role to take effect; can take a few minutes)..."
$created = $false
for ($i = 1; $i -le 12 -and -not $created; $i++) {
    az storage container create --name $container --account-name $StorageAccountName --auth-mode login 2>$null | Out-Null
    if ($LASTEXITCODE -eq 0) { $created = $true } else { Start-Sleep -Seconds 20 }
}
if (-not $created) { throw "Could not create the container yet. Wait a few minutes and re-run this script." }

Write-Host ""
Write-Host "Terraform state storage is ready." -ForegroundColor Green
Write-Host "Put these in infra\terraform\environments\<env>\backend.hcl (and in GitHub variables):"
Write-Host "  resource_group_name  = `"$ResourceGroupName`"      (TF_STATE_RESOURCE_GROUP)"
Write-Host "  storage_account_name = `"$StorageAccountName`"     (TF_STATE_STORAGE_ACCOUNT)"
Write-Host "  container_name       = `"$container`""
Write-Host "  key                  = `"<env>.terraform.tfstate`""
