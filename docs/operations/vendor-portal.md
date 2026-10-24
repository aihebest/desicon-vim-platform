# Vendor portal: setup and operation

The vendor portal (`src/Desicon.Vim.Portal`) is a separate web app where vendors sign in.
Phase 1 is **sign-in and invitations only**. Vendors cannot submit invoices yet.

| | Workbench (staff) | Vendor portal |
|---|---|---|
| Sign-in | Staff Microsoft Entra ID tenant | **Separate Microsoft Entra External ID tenant**, using an email one-time passcode |
| App name | `app-vim-<env>-weu-<suffix>` | `vendors-vim-<env>-weu-<suffix>` |
| What a user can see | Depends on their workbench role | Only their own vendor's records |

## How access works

1. **Procurement invites a contact.** They do this on the workbench page **Vendors → Portal access**. Procurement holds the `Vim.Procurement` role and must not also hold Accounts Payable.
   - Procurement picks the Business Central vendor.
   - Then they confirm the email in one of two ways:
     - The email is on the vendor card in Business Central.
     - Procurement called the vendor back on a known number and noted the call (at least 15 characters).
2. **The contact receives an email with a one-time link.** The link is valid for 7 days. The database stores only a SHA-256 hash of the token.
3. **The contact opens the link and signs in.** External ID emails them a passcode. The portal shows the invitation.
4. **The contact presses Accept.** Their login is now bound to that one vendor. The invitation's email must match the email they signed in with, so a forwarded link fails. Opening the link alone changes nothing, so a mail scanner that follows links cannot accept an invitation.
5. **Every visit is checked again.** The portal refuses access in each of these cases:
   - The user was removed.
   - The user has not signed in for 90 days. They are disabled automatically.
   - The vendor is blocked (Payment or All) or missing in Business Central.

   A removed user gets back in only through a new invitation.

Every step is written to `VendorAccessEvents`. The running apps can only add rows to that table; they cannot change or delete them.

Anyone can create an External ID account through the sign-up page. Without an accepted invitation, the account reaches nothing: the portal shows "Access needed".

## Cost

- External ID is free for the first 50,000 monthly active users, and email one-time passcodes need no SMS add-on. See [Microsoft Entra External ID pricing](https://azure.microsoft.com/en-in/pricing/details/microsoft-entra-external-id/).
- The portal runs on the **same App Service plan** as the workbench, so it adds no compute cost.

## One-time setup (per environment)

Do these steps in order. Steps 1 to 3 are in the Microsoft Entra admin center, as Global Administrator.

### 1. Create the External ID tenant (one tenant for all environments is fine)

1. Go to Entra admin center → **Entra ID → Overview → Manage tenants → Create**.
2. Choose **External**.
3. Name it, for example *Desicon Vendors*, with the domain `desiconvendors`.
4. Link it to the VIM Azure subscription.
5. Note the tenant ID and the subdomain (the part before `.ciamlogin.com`).

### 2. Turn on email one-time passcode and create the user flow

1. Switch to the new tenant (the gear icon → Directories).
2. Go to **Authentication methods → Email OTP**. Enable it for all users.
3. Go to **External Identities → User flows → New user flow**:
   - Name: `vendor-signin`.
   - Identity provider: **Email one-time passcode**.
   - Attribute to collect: Display Name.

### 3. App registration (script)

```powershell
az login --tenant <external-tenant-id> --allow-no-subscriptions
./infra/scripts/bootstrap-portal-app.ps1 -Environment dev
```

1. The script prints three `gh variable set ...` lines. Run each of them for both `dev` and `dev-plan`.
2. Go to **External Identities → User flows → vendor-signin → Applications → Add application**. Pick *Desicon VIM Vendor Portal (dev)*.

### 4. Deploy

1. Run **Deploy** (Actions → Deploy → Run workflow).
2. Terraform creates the portal app and sets `Vim__PortalUrl` on the workbench. With that set, Procurement can send invitations.
3. Terraform also grants the portal's identity access to the documents storage account.

### 5. After the first portal deploy

The deploy's portal smoke test fails until step 5a is done. That is expected; re-run Deploy afterwards.

```powershell
# a. Database user for the portal (as a member of the SQL admin group)
az login
sqlcmd -S <sql_server_fqdn> -d VimDb --authentication-method ActiveDirectoryDefault `
       -i infra\scripts\create-portal-sql-user.sql -v PortalAppName="<terraform output portal_app_name>"

# b. Redirect URIs now that the address is known (External ID tenant)
az login --tenant <external-tenant-id> --allow-no-subscriptions
./infra/scripts/bootstrap-portal-app.ps1 -Environment dev -PortalUrl https://<portal_app_name>.azurewebsites.net

# c. Let the portal read Business Central (staff tenant), so blocked vendors are refused
az login
./infra/scripts/bootstrap-bc-connector.ps1 -Environment dev
```

### 6. Give someone the Procurement role

Re-run `bootstrap-entra-app.ps1` once. This adds the `Vim.Procurement` role to the workbench app registration. Then assign the Procurement group, or a test user, to that role.

## Testing

1. Invite **your own email address**. Never use a real vendor's address in dev.
2. Open the link from that email and sign in with the passcode.
3. Press Accept.
4. Check the results:
   - The home page shows the vendor's name.
   - The workbench **Vendors → Portal access** page lists the user and the "Accepted" event.

## Routine checks

- **Quarterly:** the Finance Manager reviews **Vendors → Portal access** and removes anyone who should no longer have access.
- **When a vendor contact leaves:** Procurement removes the user, with a reason.
- **When contact details change:** Procurement invites the new contact. A new invitation replaces any pending one for the same email.
