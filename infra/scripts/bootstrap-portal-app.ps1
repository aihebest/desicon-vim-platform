<#
.SYNOPSIS
    Creates (or updates) the vendor portal's app registration in the Microsoft Entra
    External ID tenant. Run it signed in to the EXTERNAL tenant, not the staff tenant.

.DESCRIPTION
    Vendors sign in to the portal with External ID (email one-time passcode). This
    script sets up only the app registration. It creates no secret: the portal
    receives an ID token only, like the workbench.

      1. App registration "Desicon VIM Vendor Portal (<env>)", single tenant, ID tokens on.
      2. Redirect URIs: <PortalUrl>/signin-oidc and /signout-callback-oidc.
      3. Optional claim "email" in the ID token (the portal matches it to the invitation).
      4. Sign-in permissions (openid, profile, email, offline_access) approved for the
         tenant. Users in an external tenant cannot consent themselves.

    Do the tenant, sign-in method and user-flow steps first, by hand:
    docs/operations/vendor-portal.md.

.EXAMPLE
    az login --tenant <external-tenant-id> --allow-no-subscriptions
    ./infra/scripts/bootstrap-portal-app.ps1 -Environment dev -PortalUrl https://vendors-vim-dev-weu-a1b2.azurewebsites.net
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [ValidateSet('dev', 'uat', 'prod')] [string] $Environment,
    # Leave out on the first run (the app does not exist yet); re-run with it after the deploy.
    [string] $PortalUrl
)

$ErrorActionPreference = 'Stop'
$displayName = "Desicon VIM Vendor Portal ($Environment)"

$tenant = az account show --query tenantId -o tsv
if (-not $tenant) { throw "Not signed in. Run: az login --tenant <external-tenant-id> --allow-no-subscriptions" }
Write-Host "Signed in to tenant $tenant. This must be the External ID (vendor) tenant." -ForegroundColor Cyan

$tmp = [IO.Path]::GetTempPath()

# -- 1. App registration ---------------------------------------------------------------
$claimsFile = Join-Path $tmp "vim-portal-claims-$Environment.json"
[IO.File]::WriteAllText($claimsFile, (ConvertTo-Json -Depth 5 -InputObject ([ordered]@{
    idToken     = @([ordered]@{ name = 'email'; essential = $false })
    accessToken = @()
    saml2Token  = @()
})))

$appId = az ad app list --display-name $displayName --query "[0].appId" -o tsv
if (-not $appId) {
    Write-Host "Creating '$displayName'..."
    $appId = az ad app create --display-name $displayName --sign-in-audience AzureADMyOrg `
        --enable-id-token-issuance true --optional-claims "@$claimsFile" --query appId -o tsv
} else {
    Write-Host "Found '$displayName' (client ID $appId). Updating..."
    az ad app update --id $appId --enable-id-token-issuance true --optional-claims "@$claimsFile" | Out-Null
}
Remove-Item $claimsFile -ErrorAction SilentlyContinue

# -- 2. Redirect URIs (exact match only) -----------------------------------------------
if ($PortalUrl) {
    $base = $PortalUrl.TrimEnd('/')
    if ($base -notlike 'https://*') { throw "PortalUrl must start with https://" }
    $uris = @("$base/signin-oidc", "$base/signout-callback-oidc")
    Write-Host "Setting redirect URIs: $($uris -join ', ')"
    az ad app update --id $appId --web-redirect-uris @uris | Out-Null
} else {
    Write-Host "No -PortalUrl yet. Re-run with it after the portal is deployed." -ForegroundColor Yellow
}

# -- 3. Enterprise application -----------------------------------------------------------
$spId = az ad sp list --filter "appId eq '$appId'" --query "[0].id" -o tsv
if (-not $spId) { $spId = az ad sp create --id $appId --query id -o tsv }

# -- 4. Sign-in permissions, approved for the tenant ------------------------------------
$graphApi = '00000003-0000-0000-c000-000000000000'
$scopes = @(
    '37f7f235-527c-4136-accd-4a02d197296e', # openid
    '14dad69e-099b-42c9-810b-d002981feec1', # profile
    '64a6cdd6-aab1-4aaf-94b8-3cc8405e90d0', # email
    '7427e0e9-2fba-42fe-b0c0-848c9e6a8182'  # offline_access
)
$rraFile = Join-Path $tmp "vim-portal-rra-$Environment.json"
[IO.File]::WriteAllText($rraFile, (ConvertTo-Json -Depth 5 -InputObject @(
    [ordered]@{ resourceAppId = $graphApi; resourceAccess = @($scopes | ForEach-Object { [ordered]@{ id = $_; type = 'Scope' } }) })))
az ad app update --id $appId --required-resource-accesses "@$rraFile" | Out-Null
Remove-Item $rraFile -ErrorAction SilentlyContinue
Start-Sleep -Seconds 15
az ad app permission admin-consent --id $appId

$domains = az rest --method GET --url "https://graph.microsoft.com/v1.0/domains" -o json | ConvertFrom-Json
$subdomain = (($domains.value | Where-Object { $_.isInitial } | Select-Object -First 1).id) -replace '\.onmicrosoft\.com$', ''
Write-Host ""
Write-Host "Vendor portal app registration ready." -ForegroundColor Green
Write-Host "Set these GitHub variables on the '$Environment' and '$Environment-plan' environments:"
Write-Host "  gh variable set PORTAL_CLIENT_ID     --env $Environment --body $appId"
Write-Host "  gh variable set EXTERNALID_SUBDOMAIN --env $Environment --body $subdomain"
Write-Host "  gh variable set VENDOR_PORTAL_ENABLED --env $Environment --body true"
Write-Host "(repeat each with --env $Environment-plan)"
Write-Host ""
Write-Host "Then add this app to the sign-in user flow (Entra admin center > External Identities > User flows)." -ForegroundColor Yellow
