<#
.SYNOPSIS
    Lets the VIM function app read, file and send mail for ONE mailbox only:
    the shared invoice mailbox. Nothing else in Exchange Online.

.DESCRIPTION
    The function app's mailbox-intake job reads new emails from the invoice
    mailbox through Microsoft Graph, saves the attachments, and moves each
    processed email to a "Processed" folder. The notification job sends mail
    from the same mailbox.

    Graph's Mail.ReadWrite and Mail.Send "application permissions", if granted
    the usual way (Entra admin consent), apply to EVERY mailbox in the tenant
    -- including the CEO's and Finance Director's. That is far too much.

    Exchange Online "RBAC for Applications" solves this. Instead of consenting
    in Entra, you grant the permission inside Exchange, limited to a
    "management scope" that matches only the invoice mailbox. Graph then
    honours that limit.

    IMPORTANT: do NOT also grant Mail.ReadWrite / Mail.Send to this identity
    in Entra (API permissions / admin consent). Exchange adds the two grants
    together: an Entra grant would silently re-open access to every mailbox,
    and this script's scoping would no longer restrict anything. Step 6 checks
    for that.

    This script is safe to re-run; each step checks whether its object already
    exists.

.PARAMETER FunctionAppPrincipalId
    Object ID of the function app's system-assigned managed identity.
    Get it with (from infra\terraform\environments\<env>):
        terraform output -raw function_app_principal_id

.PARAMETER MailboxAddress
    The shared invoice mailbox, e.g. invoices@desicongroup.com (prod) or
    invoices-test@desicongroup.com (dev/uat). Must match Mailbox__UserId.

.PARAMETER Environment
    dev, uat or prod. Used only to give the Exchange objects readable names.

.PARAMETER TestMailboxAddress
    Optional: any OTHER mailbox. The script checks that the app is DENIED
    access to it, proving the scope works.

.EXAMPLE
    .\grant-mailbox-access.ps1 -FunctionAppPrincipalId 11111111-2222-3333-4444-555555555555 `
        -MailboxAddress invoices-test@desicongroup.com -Environment dev `
        -TestMailboxAddress someone.else@desicongroup.com

.NOTES
    Requirements (one-time, Windows PowerShell 5.1 or PowerShell 7):
      Install-Module ExchangeOnlineManagement -Scope CurrentUser
      Azure CLI (az) signed in:  az login
    You must be an Exchange Administrator (or Organization Management member).
    Permission changes can take 30 minutes to a couple of hours to take effect
    in Graph; if the function app gets "Access denied" right after running
    this, wait and retry before changing anything.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [ValidatePattern('^[0-9a-fA-F-]{36}$')] [string] $FunctionAppPrincipalId,
    [Parameter(Mandatory)] [string] $MailboxAddress,
    [Parameter(Mandatory)] [ValidateSet('dev', 'uat', 'prod')] [string] $Environment,
    [string] $TestMailboxAddress
)

$ErrorActionPreference = 'Stop'

# ----------------------------------------------------------------------------
# Step 1: Look up the managed identity's two IDs.
#
# Every managed identity is a "service principal" in Entra with:
#   - an Object ID (what Terraform outputs as principal_id), and
#   - an Application (client) ID ("appId").
# Exchange needs both to register it.
# ----------------------------------------------------------------------------
Write-Host "Step 1: Looking up the managed identity in Entra..." -ForegroundColor Cyan
$sp = az ad sp show --id $FunctionAppPrincipalId --query "{appId:appId, displayName:displayName}" -o json | ConvertFrom-Json
if (-not $sp) { throw "No service principal found with object ID $FunctionAppPrincipalId. Is 'az login' signed in to the right tenant?" }
Write-Host "  Found '$($sp.displayName)' (appId $($sp.appId))"

# ----------------------------------------------------------------------------
# Step 2: Connect to Exchange Online (opens a sign-in window).
# ----------------------------------------------------------------------------
Write-Host "Step 2: Connecting to Exchange Online..." -ForegroundColor Cyan
Import-Module ExchangeOnlineManagement
Connect-ExchangeOnline -ShowBanner:$false

try {
    # ------------------------------------------------------------------------
    # Step 3: Register the identity with Exchange.
    #
    # Exchange keeps its own pointer to Entra service principals. This creates
    # that pointer; it grants nothing by itself.
    # ------------------------------------------------------------------------
    Write-Host "Step 3: Registering the identity with Exchange..." -ForegroundColor Cyan
    $exoSp = Get-ServicePrincipal -Identity $FunctionAppPrincipalId -ErrorAction SilentlyContinue
    if (-not $exoSp) {
        $null = New-ServicePrincipal -AppId $sp.appId -ObjectId $FunctionAppPrincipalId -DisplayName $sp.displayName
        Write-Host "  Registered."
    } else {
        Write-Host "  Already registered."
    }

    # ------------------------------------------------------------------------
    # Step 4: Create a management scope that matches ONLY the invoice mailbox.
    #
    # A scope is a saved filter over mailboxes. This one matches exactly one
    # mailbox by its primary SMTP address. Whatever we grant in step 5 is
    # limited to the mailboxes this filter matches.
    # ------------------------------------------------------------------------
    Write-Host "Step 4: Creating the mailbox scope..." -ForegroundColor Cyan
    $mailbox = Get-Mailbox -Identity $MailboxAddress
    $primary = $mailbox.PrimarySmtpAddress.ToString()
    $scopeName = "VIM $Environment - invoice mailbox only"
    $filter = "PrimarySmtpAddress -eq '$primary'"

    $scope = Get-ManagementScope -Identity $scopeName -ErrorAction SilentlyContinue
    if (-not $scope) {
        $null = New-ManagementScope -Name $scopeName -RecipientRestrictionFilter $filter
        Write-Host "  Created scope '$scopeName' ($filter)."
    } elseif ($scope.RecipientFilter -ne $filter) {
        Set-ManagementScope -Identity $scopeName -RecipientRestrictionFilter $filter
        Write-Host "  Updated scope filter to: $filter"
    } else {
        Write-Host "  Scope already exists."
    }

    # ------------------------------------------------------------------------
    # Step 5: Grant the two application roles, limited to that scope.
    #
    #   "Application Mail.ReadWrite" = read mail, save attachments, move the
    #                                  email to the Processed folder.
    #   "Application Mail.Send"      = send notifications from this mailbox.
    # ------------------------------------------------------------------------
    Write-Host "Step 5: Granting scoped permissions..." -ForegroundColor Cyan
    foreach ($role in @('Application Mail.ReadWrite', 'Application Mail.Send')) {
        $assignmentName = "VIM $Environment - $role"
        $existing = Get-ManagementRoleAssignment -Identity $assignmentName -ErrorAction SilentlyContinue
        if (-not $existing) {
            New-ManagementRoleAssignment -Name $assignmentName -App $FunctionAppPrincipalId `
                -Role $role -CustomResourceScope $scopeName | Out-Null
            Write-Host "  Granted '$role'."
        } else {
            Write-Host "  '$role' already granted."
        }
    }

    # ------------------------------------------------------------------------
    # Step 6: Prove it. The app must be allowed on the invoice mailbox and
    # (if a test mailbox was given) denied everywhere else.
    # ------------------------------------------------------------------------
    Write-Host "Step 6: Verifying..." -ForegroundColor Cyan
    Test-ServicePrincipalAuthorization -Identity $FunctionAppPrincipalId -Resource $primary |
        Format-Table RoleName, GrantedPermissions, AllowedResourceScope, InScope -AutoSize

    if ($TestMailboxAddress) {
        Write-Host "  Checking that '$TestMailboxAddress' is OUT of scope (InScope should be False):"
        Test-ServicePrincipalAuthorization -Identity $FunctionAppPrincipalId -Resource $TestMailboxAddress |
            Format-Table RoleName, InScope -AutoSize
    }

    # Warn if someone also granted tenant-wide Graph mail permissions in Entra.
    $entraGrants = az rest --method GET `
        --url "https://graph.microsoft.com/v1.0/servicePrincipals/$FunctionAppPrincipalId/appRoleAssignments" `
        --query "value[?resourceDisplayName=='Microsoft Graph'].appRoleId" -o json | ConvertFrom-Json
    if ($entraGrants -and $entraGrants.Count -gt 0) {
        Write-Warning ("This identity ALSO has Microsoft Graph application permissions granted in Entra " +
            "(app role IDs: $($entraGrants -join ', ')). If any of these are Mail.* permissions, " +
            "they apply to EVERY mailbox and override the scoping above. Remove them in " +
            "Entra admin center > Enterprise applications > $($sp.displayName) > Permissions.")
    } else {
        Write-Host "  No tenant-wide Graph permissions in Entra. Good." -ForegroundColor Green
    }

    Write-Host "Done. Allow up to 2 hours for Graph to apply the new permissions." -ForegroundColor Green
}
finally {
    Disconnect-ExchangeOnline -Confirm:$false
}
