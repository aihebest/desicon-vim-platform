# Short addresses (custom domains)

The apps always answer on their long `azurewebsites.net` addresses. A short address can be added per environment; emails and vendor invitation links then use it. The long addresses keep working.

| App | Production address | DNS managed at |
|---|---|---|
| Vendor portal | `vendors.desicongroup.com` | Cloudflare |
| Workbench (staff) | `vim.desiconapp.com` | Microsoft 365 admin center |

**Cost:** none. The certificate is App Service's free managed certificate, renewed by Azure.

The vendor sign-in page itself stays on `desiconvendors.ciamlogin.com`. Moving that page to a custom address needs Azure Front Door, which is a paid service and not worth it here.

## Steps, in this order

### 1. DNS records (before any deploy)

Get the verification ID (the same value for both apps):

```powershell
az webapp show -g rg-vim-prod-weu -n vendors-vim-prod-weu-izpl --query customDomainVerificationId -o tsv
```

For each address add two records:

| Type | Name | Value |
|---|---|---|
| CNAME | `vendors` | `vendors-vim-prod-weu-izpl.azurewebsites.net` |
| TXT | `asuid.vendors` | the verification ID |
| CNAME | `vim` | `app-vim-prod-weu-izpl.azurewebsites.net` |
| TXT | `asuid.vim` | the verification ID |

In Cloudflare the CNAME must be **DNS only** (grey cloud). A proxied record stops Azure from issuing the certificate. If the domain has CAA records, `digicert.com` must be among the allowed issuers.

### 2. GitHub variables

```powershell
foreach ($e in "prod-plan","prod") {
  gh variable set PORTAL_HOSTNAME    --repo aihebest/desicon-vim-platform --env $e --body "vendors.desicongroup.com"
  gh variable set WORKBENCH_HOSTNAME --repo aihebest/desicon-vim-platform --env $e --body "vim.desiconapp.com"
}
```

### 3. Sign-in addresses

Both the long and the short address are registered, so either works.

```powershell
# Workbench (staff tenant)
az login --tenant 77e479f1-fefd-4238-a1a9-6b1f692f20b8
.\infra\scripts\bootstrap-entra-app.ps1 -Environment prod -WebAppUrls "https://app-vim-prod-weu-izpl.azurewebsites.net","https://vim.desiconapp.com"

# Vendor portal (vendor tenant)
az login --tenant 31a1b00e-9c2a-4457-a4aa-db82ea2673a2 --allow-no-subscriptions
.\infra\scripts\bootstrap-portal-app.ps1 -Environment prod -PortalUrl "https://vendors-vim-prod-weu-izpl.azurewebsites.net","https://vendors.desicongroup.com"
az login --tenant 77e479f1-fefd-4238-a1a9-6b1f692f20b8
```

### 4. Deploy

Run **Deploy** with Last environment = `prod`. Terraform binds each address, issues its certificate, and sets `Vim__PortalUrl` and `Vim__WorkbenchUrl` to the short addresses. The certificate can take a few minutes; if the apply times out on it, start a new run.

## Removing a short address

Clear the GitHub variable and deploy; then delete the DNS records. Do it in that order, so no DNS record is left pointing at an app that no longer claims it.
