# Vendor portal: setup and operation

The vendor portal (`src/Desicon.Vim.Portal`) is a separate web app where vendors sign in.
Vendors sign in (phase 1), then submit invoices with the agreed documents, follow their status and correct returned invoices (phase 2).

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
     - Procurement called the vendor back on a known number and noted the call (at least 15 characters). **The Finance Manager is emailed about every call-back invitation** and can withdraw it before it is used.
   - Desicon staff addresses (`Vim:StaffEmailDomains`, set to `desicongroup.com` and `desiconerp.com`) can never be invited, so no employee can hold a vendor login.
2. **The contact receives an email with a one-time link.** The link is valid for 7 days. The database stores only a SHA-256 hash of the token. The email is not kept in the shared mailbox's Sent Items, and the link is removed from the stored copy once sent.
3. **The contact opens the link and signs in.** External ID emails them a passcode. The portal shows the invitation.
4. **The contact presses Accept.** Their login is now bound to that one vendor. The invitation's email must match the email they signed in with, so a forwarded link fails. Opening the link alone changes nothing, so a mail scanner that follows links cannot accept an invitation.
5. **Every visit is checked again.** The portal refuses access in each of these cases:
   - The user was removed.
   - The user has not signed in for 90 days. They are disabled automatically.
   - The vendor is blocked (Payment or All) or missing in Business Central.

   A removed user gets back in only through a new invitation.

Every step is written to `VendorAccessEvents`. The running apps can only add rows to that table; they cannot change or delete them.

Anyone can create an External ID account through the sign-up page. Without an accepted invitation, the account reaches nothing: the portal shows "Access needed".

## What a vendor can do (phase 2)

- **Submit an invoice.**
  1. Choose *Materials (PO)* or *Services (SO)*.
  2. Type the invoice details.
  3. Upload one PDF per document slot, from the agreed list in `RequiredDocuments`:
     - **Materials:** invoice, waybill or delivery note, signed GRV, PO.
     - **Services:** invoice, signed Work Completion Certificate or TRV, Service Order.
     - Optional extras either way.
  4. Before anything is recorded, the same checks as Accounts Payable's assisted submission run: all slots filled, real PDFs, the PO is the vendor's and released, the amount equals the received-not-invoiced value, and the invoice is not a duplicate.
  5. The vendor gets a reference and an acknowledgement email at their login address.
- **Track invoices.** **My invoices** lists every invoice for their vendor account with a plain-words status. The page reminds vendors that **credit days count from acceptance**.
- **Correct a returned invoice.**
  1. The invoice page shows *why* it was returned.
  2. The vendor uploads only the corrected documents and/or fixes the details.
  3. It keeps the same reference, and the automatic checks run again.

What the portal can and cannot touch:

| | Can | Cannot |
|---|---|---|
| Invoices | Its own vendor's cases only (the vendor number comes from the login, never the form; another vendor's case shows "not found") | See reviewers, internal notes, checklist answers or other vendors |
| Documents | Upload new PDFs (Storage Blob Data Contributor on the documents container only; no portal code reads, lists or deletes files; versioning and soft delete keep files recoverable) | Touch any other container or storage account |
| Database | Its own role `vim_portal` (see `create-portal-sql-user.sql`) | Read the outbox or routes, change audit history, change the schema |

After deploying phase 2, **re-run `create-portal-sql-user.sql`** (same command as step 5a below) to grant the submission permissions.

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
   - **Do not add Google, Facebook or any other identity provider.** The portal refuses those sign-ins. Only the email passcode proves the person controls the invited address.

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
3. The portal gets **no** Azure storage or Key Vault access in phase 1. Its only data access is its own database tables (step 5a).

### 5. After the first portal deploy

The deploy's portal smoke test fails until step 5a is done. That is expected; re-run Deploy afterwards.

```powershell
# a. Database user for the portal (as a member of the SQL admin group).
#    It can only read and write the vendor invitation and user tables, and add to the access log.
az login
sqlcmd -S <sql_server_fqdn> -d VimDb --authentication-method ActiveDirectoryDefault `
       -i infra\scripts\create-portal-sql-user.sql -v PortalAppName="<terraform output portal_app_name>"

# b. Redirect URIs now that the address is known (External ID tenant)
az login --tenant <external-tenant-id> --allow-no-subscriptions
./infra/scripts/bootstrap-portal-app.ps1 -Environment dev -PortalUrl https://<portal_app_name>.azurewebsites.net

# c. Let the portal check Business Central (staff tenant), so blocked vendors are refused.
#    It uses the same read-only connector as the workbench.
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

## If a vendor cannot sign in

- **"The sign-in token has no email address":** the `email` optional claim is missing. Re-run `bootstrap-portal-app.ps1`.
- **"Sign in with your email address and one-time passcode":** the person used a social or other identity provider. Remove that provider from the user flow.

## Routine checks

- **Quarterly:** the Finance Manager reviews **Vendors → Portal access** and removes anyone who should no longer have access.
- **When a vendor contact leaves:** Procurement removes the user, with a reason.
- **When contact details change:** Procurement invites the new contact. A new invitation replaces any pending one for the same email.
