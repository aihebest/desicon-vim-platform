<#
.SYNOPSIS
    Creates (or updates) the app registration the VIM platform uses to READ
    Business Central, with no client secret.

.DESCRIPTION
    Business Central lives in its own Entra tenant (desiconerp.com); the VIM
    apps run in Desicon's main tenant. This script sets up the same secretless
    pattern the Cost Control platform (DCCP) uses:

      VIM web/function app managed identity
        -> trusted by a federated credential on this "connector" app (main tenant)
        -> connector is consented in the BC tenant and enabled inside BC
        -> token for the Business Central API

    Nothing secret is created, stored or rotated.

    What this script does (main tenant, run as an admin who can create app registrations):
      1. Creates "Desicon VIM Business Central connector (<env>)", multi-tenant,
         requesting the Business Central API application permission.
      2. Adds one federated credential per VIM app (web + functions, and the vendor
         portal when it exists -- re-run after enabling it), trusting
         each app's system-assigned managed identity.
      3. Prints the admin-consent link for the BC tenant and the BC steps.

    What YOU then do once (as Global Administrator of the BC tenant):
      a. Open the printed consent link and accept.
      b. In Business Central (the right environment: Sandbox for dev/uat,
         Production for prod) open "Microsoft Entra Applications", New:
         Client ID = the printed client ID, State = Enabled,
         permission set D365 READ (company: Desicon Engineering Ltd).
      c. Set the GitHub variable BC_CLIENT_ID (command printed at the end).

    Business Central has no read-only Entra permission, so the Entra side asks
    for API.ReadWrite.All; what the connector can actually do is limited inside
    BC by the D365 READ permission set. Never give it SUPER or a write set.

    Safe to re-run: it finds the app by display name and adds only what is missing.

.PARAMETER Environment
    dev, uat or prod.
.PARAMETER BcTenant
    The Business Central tenant (domain or id). Default desiconerp.com.
.PARAMETER ResourceGroup
    Resource group of the VIM apps. Default rg-vim-<env>-weu.
.EXAMPLE
    .\bootstrap-bc-connector.ps1 -Environment dev
.NOTES
    Needs Azure CLI signed in to the MAIN tenant (az login), with rights to
    create app registrations and read the VIM resource group. Run after the
    first Terraform apply (the apps and their managed identities must exist).
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [ValidateSet('dev', 'uat', 'prod')] [string] $Environment,
    [string] $BcTenant = 'desiconerp.com',
    [string] $ResourceGroup = ''
)

$ErrorActionPreference = 'Stop'
if (-not $ResourceGroup) { $ResourceGroup = "rg-vim-$Environment-weu" }
$displayName = "Desicon VIM Business Central connector ($Environment)"

# Business Central API (first-party app) and its API.ReadWrite.All application role. Fixed, same in every tenant.
$bcResourceApp = '996def3d-b36c-4153-8607-a6fd3c01b89f'
$bcAppRole = 'a42b0b75-311e-488d-b67e-8fe84f924341'

$homeTenant = az account show --query tenantId -o tsv
if (-not $homeTenant) { throw "Not signed in. Run: az login" }
Write-Host "Main tenant: $homeTenant   Business Central tenant: $BcTenant" -ForegroundColor Cyan

# -- The managed identities that will call Business Central ----------------------
# Pick web apps by name (the workbench is app-*, the vendor portal vendors-*), filtering in
# PowerShell so the query has no brackets to trip over Windows quoting.
$webApps = @(az webapp list -g $ResourceGroup -o json | ConvertFrom-Json)
$webPrincipal = ($webApps | Where-Object { $_.name -like 'app-*' } | Select-Object -First 1).identity.principalId
$portalPrincipal = ($webApps | Where-Object { $_.name -like 'vendors-*' } | Select-Object -First 1).identity.principalId
$funcPrincipal = az functionapp list -g $ResourceGroup --query "[0].identity.principalId" -o tsv
if (-not $webPrincipal -or -not $funcPrincipal) {
    throw "Could not find the web app and function app managed identities in $ResourceGroup. Run the Terraform deployment first."
}
Write-Host "Web app identity:      $webPrincipal"
Write-Host "Function app identity: $funcPrincipal"
if ($portalPrincipal) { Write-Host "Vendor portal identity: $portalPrincipal" }

# -- 1. Connector app registration -------------------------------------------------
$tmp = [IO.Path]::GetTempPath()
$rraFile = Join-Path $tmp "vim-bc-rra-$Environment.json"
# WriteAllText: UTF-8 without a BOM (Set-Content -Encoding utf8 adds one in Windows PowerShell 5.1).
[IO.File]::WriteAllText($rraFile, (ConvertTo-Json -Depth 5 -InputObject @(
    [ordered]@{ resourceAppId = $bcResourceApp; resourceAccess = @([ordered]@{ id = $bcAppRole; type = 'Role' }) })))

$appId = az ad app list --display-name $displayName --query "[0].appId" -o tsv
if (-not $appId) {
    Write-Host "Creating '$displayName'..."
    $appId = az ad app create --display-name $displayName --sign-in-audience AzureADMultipleOrgs `
        --web-redirect-uris "https://businesscentral.dynamics.com/OAuthLanding.htm" `
        --required-resource-accesses "@$rraFile" --query appId -o tsv
} else {
    Write-Host "Found '$displayName' (client ID $appId). Making sure its settings are right..."
    az ad app update --id $appId --sign-in-audience AzureADMultipleOrgs `
        --web-redirect-uris "https://businesscentral.dynamics.com/OAuthLanding.htm" `
        --required-resource-accesses "@$rraFile" | Out-Null
}
Remove-Item $rraFile -ErrorAction SilentlyContinue

# -- 2. Federated credentials: trust each app's managed identity --------------------
$existing = @(az ad app federated-credential list --id $appId --query "[].name" -o tsv)
function Add-Trust([string] $name, [string] $principalId, [string] $what) {
    if ($existing -contains $name) { Write-Host "  Trust for $what exists ($name)"; return }
    $file = Join-Path $tmp "vim-bc-fic-$name.json"
    [IO.File]::WriteAllText($file, (ConvertTo-Json -InputObject ([ordered]@{
        name        = $name
        issuer      = "https://login.microsoftonline.com/$homeTenant/v2.0"
        subject     = $principalId
        description = "VIM $what ($Environment) managed identity -> Business Central"
        audiences   = @('api://AzureADTokenExchange')
    })))
    az ad app federated-credential create --id $appId --parameters "@$file" | Out-Null
    Remove-Item $file -ErrorAction SilentlyContinue
    Write-Host "  Trusts the $what managed identity"
}
Add-Trust "vim-web-$Environment" $webPrincipal 'web app'
Add-Trust "vim-func-$Environment" $funcPrincipal 'function app'
# The portal reads BC only to refuse vendors that are blocked there.
if ($portalPrincipal) { Add-Trust "vim-portal-$Environment" $portalPrincipal 'vendor portal' }

# -- 3. What to do next ----------------------------------------------------------------
$bcEnv = if ($Environment -eq 'prod') { 'Production' } else { 'Sandbox' }
Write-Host ""
Write-Host "Connector ready. Client ID: $appId" -ForegroundColor Green
Write-Host ""
Write-Host "Next, as Global Administrator of $BcTenant :" -ForegroundColor Yellow
Write-Host "  a. Open this link, sign in with your $BcTenant admin account, and Accept:"
Write-Host "     https://login.microsoftonline.com/$BcTenant/adminconsent?client_id=$appId"
Write-Host "  b. Business Central ($bcEnv) > Microsoft Entra Applications > New:"
Write-Host "     Client ID $appId, State Enabled, permission set D365 READ, company Desicon Engineering Ltd"
Write-Host "  c. Tell GitHub the connector's client ID:"
Write-Host "     foreach (`$e in '$Environment-plan','$Environment') { gh variable set BC_CLIENT_ID --repo aihebest/desicon-vim-platform --env `$e --body $appId }"
