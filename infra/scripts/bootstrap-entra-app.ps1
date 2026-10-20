<#
.SYNOPSIS
    Creates (or updates) the Entra ID app registration staff use to sign in to
    the VIM workbench, with the platform's six app roles.

.DESCRIPTION
    An "app registration" is how Entra ID knows about an application. The VIM
    web app uses it to sign staff in (Microsoft.Identity.Web, sign-in only --
    no client secret, because the app never calls another API as the user).

    App roles are the platform's job titles. When an admin assigns a person
    (or better, a security group) to a role on the "Enterprise application",
    Entra puts that role in the user's sign-in token, and the app uses it to
    decide what the person may see and do:

      Vim.DocumentController  Registers and checks incoming invoice documents
      Vim.Reviewer            Departmental verification (e.g. confirms delivery/GRV)
      Vim.AccountsPayable     Prepares the verified package for AP / Business Central
      Vim.FinanceManager      Finance oversight, exceptions, approvals
      Vim.PlatformAdmin       Configuration (routes, users, reference data)
      Vim.Auditor             Read-only access to cases and audit history

    The role IDs below are FIXED GUIDs. Never change them: Entra links every
    existing assignment to the ID, so a new ID would orphan every assignment.
    To add a role, add a new entry with a new GUID.

    "Assignment required" is switched on, so ONLY people assigned a role can
    sign in at all. Everyone else in the tenant gets an Entra error page.

    Safe to re-run: it finds the app by display name and updates it.

.PARAMETER Environment
    dev, uat or prod. The display name becomes "Desicon VIM Workbench (<env>)".

.PARAMETER WebAppUrls
    The workbench's URL(s), e.g. https://app-vim-dev-weu-a1b2.azurewebsites.net
    (from: terraform output -raw web_app_url). "/signin-oidc" is appended.
    Leave empty on the very first run (before terraform apply), then run again
    with the URL once the web app exists.

.PARAMETER LocalDevUrls
    Developer machine URLs for local debugging, dev only, e.g.
    https://localhost:5001 -- must match the Web project's launchSettings.json.

.PARAMETER RoleGroups
    Optional hashtable mapping role value -> Entra security group object ID,
    e.g. @{ 'Vim.PlatformAdmin' = '<group-object-id>'; 'Vim.Auditor' = '<id>' }.
    Assigns each group to that role.

.EXAMPLE
    # First run, before Terraform (prints the client ID for entra_workbench_client_id):
    .\bootstrap-entra-app.ps1 -Environment dev -LocalDevUrls https://localhost:5001

.EXAMPLE
    # After terraform apply, add the real URL and assign groups:
    .\bootstrap-entra-app.ps1 -Environment dev `
        -WebAppUrls https://app-vim-dev-weu-a1b2.azurewebsites.net `
        -LocalDevUrls https://localhost:5001 `
        -RoleGroups @{ 'Vim.PlatformAdmin' = '00000000-0000-0000-0000-000000000000' }

.NOTES
    Requires Azure CLI signed in with rights to create app registrations
    (Application Administrator or Cloud Application Administrator):  az login
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [ValidateSet('dev', 'uat', 'prod')] [string] $Environment,
    [string[]] $WebAppUrls = @(),
    [string[]] $LocalDevUrls = @(),
    [hashtable] $RoleGroups = @{}
)

$ErrorActionPreference = 'Stop'
$displayName = "Desicon VIM Workbench ($Environment)"

if ($Environment -eq 'prod' -and $LocalDevUrls.Count -gt 0) {
    throw "Local developer redirect URLs must not be registered on the prod app."
}

# -- App roles (fixed IDs -- see description) ------------------------------------
$roles = @(
    @{ id = '5ae83993-a5bf-46c0-a923-beab918b9d04'; value = 'Vim.DocumentController'; displayName = 'Document Controller'
       description = 'Registers incoming vendor invoices and checks supporting documents.' },
    @{ id = '99d6fcd2-f53c-4539-ac7b-28c3574ff9d9'; value = 'Vim.Reviewer'; displayName = 'Departmental Reviewer'
       description = 'Verifies invoices for their department (delivery, GRV, service completion).' },
    @{ id = '7627cedc-a68b-4634-b6c4-6415d19aeb37'; value = 'Vim.AccountsPayable'; displayName = 'Accounts Payable'
       description = 'Prepares verified invoice packages for Accounts Payable and Business Central.' },
    @{ id = '421ef1f1-ccef-4416-a1e8-5fdba824d1c7'; value = 'Vim.FinanceManager'; displayName = 'Finance Manager'
       description = 'Finance oversight: exceptions, escalations and approvals.' },
    @{ id = 'fecec827-dc01-4c19-b112-0b62d350ff14'; value = 'Vim.PlatformAdmin'; displayName = 'Platform Administrator'
       description = 'Configures routes, reference data and platform settings.' },
    @{ id = '626a6f2c-f6c2-422c-a081-363714aa6cc5'; value = 'Vim.Auditor'; displayName = 'Auditor'
       description = 'Read-only access to cases and the audit trail.' }
) | ForEach-Object {
    [ordered]@{
        allowedMemberTypes = @('User')   # people and groups, not applications
        description        = $_.description
        displayName        = $_.displayName
        id                 = $_.id
        isEnabled          = $true
        value              = $_.value
    }
}

$rolesFile = Join-Path ([IO.Path]::GetTempPath()) "vim-app-roles-$Environment.json"
# WriteAllText: UTF-8 without a BOM (Set-Content -Encoding utf8 adds one in 5.1).
[IO.File]::WriteAllText($rolesFile, (ConvertTo-Json -InputObject @($roles) -Depth 5))

$redirectUris = @($WebAppUrls + $LocalDevUrls | ForEach-Object { $_.TrimEnd('/') + '/signin-oidc' })

# -- 1. Create or update the app registration ----------------------------------
Write-Host "Looking for app registration '$displayName'..." -ForegroundColor Cyan
$appId = az ad app list --display-name $displayName --query "[0].appId" -o tsv

if (-not $appId) {
    Write-Host "Creating it..."
    $appId = az ad app create `
        --display-name $displayName `
        --sign-in-audience AzureADMyOrg `
        --enable-id-token-issuance true `
        --app-roles "@$rolesFile" `
        --query appId -o tsv
} else {
    Write-Host "Found (client ID $appId). Updating roles..."
    az ad app update --id $appId --app-roles "@$rolesFile" --enable-id-token-issuance true | Out-Null
}

# Redirect URIs: where Entra sends the browser back after sign-in. Only these
# exact URLs are accepted, which stops a sign-in being redirected elsewhere.
if ($redirectUris.Count -gt 0) {
    Write-Host "Setting redirect URIs: $($redirectUris -join ', ')"
    az ad app update --id $appId --web-redirect-uris @redirectUris | Out-Null
} else {
    Write-Host "No redirect URIs given yet. Re-run with -WebAppUrls after terraform apply." -ForegroundColor Yellow
}

Remove-Item $rolesFile -ErrorAction SilentlyContinue

# -- 2. Enterprise application (service principal) -----------------------------
# The app registration is the definition; the service principal is the copy
# that lives in the tenant, where users and groups are assigned to roles.
$spId = az ad sp list --filter "appId eq '$appId'" --query "[0].id" -o tsv
if (-not $spId) {
    Write-Host "Creating the enterprise application..."
    $spId = az ad sp create --id $appId --query id -o tsv
}

# Only users/groups assigned a role may sign in.
az ad sp update --id $spId --set appRoleAssignmentRequired=true | Out-Null
Write-Host "Assignment required: on."

# -- 2b. Sign-in permissions, approved for the whole organisation ----------------
# Without these listed and admin-consented, every non-admin user sees
# "Need admin approval" at sign-in. Delegated Microsoft Graph scopes only:
# openid, profile, email, offline_access, User.Read (no mail or file access).
$graphApi = '00000003-0000-0000-c000-000000000000'
$signInScopes = @(
    '37f7f235-527c-4136-accd-4a02d197296e', # openid
    '14dad69e-099b-42c9-810b-d002981feec1', # profile
    '64a6cdd6-aab1-4aaf-94b8-3cc8405e90d0', # email
    '7427e0e9-2fba-42fe-b0c0-848c9e6a8182', # offline_access
    'e1fe6dd8-ba31-4d61-89e7-88639da4683d'  # User.Read
)
$rraFile = Join-Path ([IO.Path]::GetTempPath()) "vim-app-rra-$Environment.json"
[IO.File]::WriteAllText($rraFile, (ConvertTo-Json -Depth 5 -InputObject @(
    [ordered]@{ resourceAppId = $graphApi; resourceAccess = @($signInScopes | ForEach-Object { [ordered]@{ id = $_; type = 'Scope' } }) })))
az ad app update --id $appId --required-resource-accesses "@$rraFile" | Out-Null
Remove-Item $rraFile -ErrorAction SilentlyContinue
Start-Sleep -Seconds 15 # let Entra replicate the change before consenting
az ad app permission admin-consent --id $appId
Write-Host "Sign-in permissions approved for the organisation."

# -- 3. Optional: assign groups to roles -----------------------------------------
foreach ($roleValue in $RoleGroups.Keys) {
    $role = $roles | Where-Object { $_.value -eq $roleValue }
    if (-not $role) { throw "Unknown role '$roleValue'. Valid: $($roles.value -join ', ')" }
    $groupId = $RoleGroups[$roleValue]

    $already = az rest --method GET `
        --url "https://graph.microsoft.com/v1.0/servicePrincipals/$spId/appRoleAssignedTo" `
        --query "value[?principalId=='$groupId' && appRoleId=='$($role.id)'] | length(@)" -o tsv
    if ($already -eq '0') {
        $bodyFile = Join-Path ([IO.Path]::GetTempPath()) "vim-role-assignment.json"
        [IO.File]::WriteAllText($bodyFile,
            (@{ principalId = $groupId; resourceId = $spId; appRoleId = $role.id } | ConvertTo-Json))
        az rest --method POST `
            --url "https://graph.microsoft.com/v1.0/servicePrincipals/$spId/appRoleAssignedTo" `
            --headers "Content-Type=application/json" --body "@$bodyFile" | Out-Null
        Remove-Item $bodyFile -ErrorAction SilentlyContinue
        Write-Host "Assigned group $groupId to $roleValue."
    } else {
        Write-Host "Group $groupId already has $roleValue."
    }
}

Write-Host ""
Write-Host "Workbench app registration ready." -ForegroundColor Green
Write-Host "  Display name : $displayName"
Write-Host "  Client ID    : $appId   <-- set this as entra_workbench_client_id (Terraform) / ENTRA_WORKBENCH_CLIENT_ID (GitHub)"
Write-Host "  Tenant ID    : $(az account show --query tenantId -o tsv)"
