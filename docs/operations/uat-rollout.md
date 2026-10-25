# UAT rollout runbook (and the prod checklist)

This is the ordered list for standing up **UAT**, where Finance tests with real (anonymised) invoices before go-live. Prod uses the same steps at go-live, plus the extras at the end.

- **UAT is sized like dev:** one B1 App Service plan and a serverless SQL database, so it costs about the same per month.
- **Time:** about half a day. Most of it is waiting for Azure and for permissions to propagate.

Run everything in **PowerShell, from the repo folder**, signed in as **Global Administrator / subscription Owner**. Background on each step is in [infrastructure.md](infrastructure.md) §4.

**What UAT reuses from dev, and what it gets new:**

| Reuses | Gets new |
|---|---|
| Subscription and tenant | Resource group `rg-vim-uat-weu` and everything in it |
| Terraform state account | Its own state file `uat.terraform.tfstate` |
| Workbench role groups (`grp-vim-accountspayable`, …) | SQL and Key Vault admin groups named `…-uat` |
| **Desicon Vendors** External ID tenant and its `vendor-signin` flow | Workbench app registration `(uat)`, vendor portal app registration `(uat)`, BC connector `(uat)` |
| BC environment *Sandbox / ProdCopy* (UAT tests against the BC copy) | Sender mailbox **`invoices@desicongroup.com`** |

---

## 0. Tools on this laptop (once)

```powershell
winget install Microsoft.AzureCLI
winget install GitHub.cli
winget install Hashicorp.Terraform
winget install sqlcmd
```

Close and reopen the terminal afterwards. Then sign in to both:

```powershell
az login --tenant 77e479f1-fefd-4238-a1a9-6b1f692f20b8
gh auth login
```

## 1. Groups and CI identities

```powershell
cd infra\scripts
az ad group create --display-name "grp-vim-sql-admins-uat" --mail-nickname "grp-vim-sql-admins-uat"
az ad group create --display-name "grp-vim-kv-secrets-uat" --mail-nickname "grp-vim-kv-secrets-uat"
$me = az ad signed-in-user show --query id -o tsv
az ad group member add --group "grp-vim-sql-admins-uat" --member-id $me
az ad group member add --group "grp-vim-kv-secrets-uat" --member-id $me
$sqlGroup = az ad group show --group "grp-vim-sql-admins-uat" --query id -o tsv
$kvGroup  = az ad group show --group "grp-vim-kv-secrets-uat" --query id -o tsv

# The state account name is in dev's backend.hcl (storage_account_name).
.\bootstrap-github-oidc.ps1 -Environment uat -GitHubRepository aihebest/desicon-vim-platform -StateStorageAccountName <state account>
```

Note the two app IDs the script prints: the **deploy** identity and the **migration** identity.

## 2. Workbench app registration (uat)

```powershell
.\bootstrap-entra-app.ps1 -Environment uat
```

Note the **Client ID**. This creates *Desicon VIM Workbench (uat)* with all roles, including Procurement.

## 3. First Terraform apply, from this PC (not the pipeline)

This step must run **as you, not the pipeline**, because it creates a custom role (*VIM Blob Index Tag Reader*). The pipeline's deploy identity is deliberately not allowed to create roles. After this one run, the pipeline handles everything.

```powershell
cd ..\terraform\environments\uat
Copy-Item backend.hcl.example backend.hcl                 # set storage_account_name
Copy-Item example.auto.tfvars.example uat.auto.tfvars     # fill in the values below
```

In `uat.auto.tfvars`:

- `subscription_id = "37d16793-ba1b-4af9-bc77-decd1f116ee1"`
- `sql_admin_group_name = "grp-vim-sql-admins-uat"`
- `sql_admin_group_object_id`: the value of `$sqlGroup`
- `entra_workbench_client_id`: from step 2
- `keyvault_secrets_officer_object_ids = ["<$kvGroup>"]`
- `business_central_client_id = ""` for now (step 6 fills it in)
- `mailbox_user_id = "invoices@desicongroup.com"`
- `sql_allowed_ip_addresses = ["<your IP: (curl.exe -s https://api.ipify.org)>"]`

```powershell
terraform init -backend-config=backend.hcl
terraform plan -out tfplan
terraform apply tfplan
terraform output
```

## 4. GitHub environments and variables

```powershell
$repo = "aihebest/desicon-vim-platform"
gh api -X PUT "repos/$repo/environments/uat-plan" | Out-Null
gh api -X PUT "repos/$repo/environments/uat"      | Out-Null
foreach ($e in "uat-plan","uat") {
  gh variable set AZURE_TENANT_ID           --repo $repo --env $e --body "77e479f1-fefd-4238-a1a9-6b1f692f20b8"
  gh variable set AZURE_SUBSCRIPTION_ID     --repo $repo --env $e --body "37d16793-ba1b-4af9-bc77-decd1f116ee1"
  gh variable set AZURE_CLIENT_ID           --repo $repo --env $e --body "<deploy identity appId from step 1>"
  gh variable set TF_STATE_RESOURCE_GROUP   --repo $repo --env $e --body "rg-vim-tfstate"
  gh variable set TF_STATE_STORAGE_ACCOUNT  --repo $repo --env $e --body "<state account>"
  gh variable set SQL_ADMIN_GROUP_NAME      --repo $repo --env $e --body "grp-vim-sql-admins-uat"
  gh variable set SQL_ADMIN_GROUP_OBJECT_ID --repo $repo --env $e --body $sqlGroup
  gh variable set ENTRA_WORKBENCH_CLIENT_ID --repo $repo --env $e --body "<from step 2>"
  gh variable set BC_TENANT_ID              --repo $repo --env $e --body "desiconerp.com"
  gh variable set BC_ENVIRONMENT            --repo $repo --env $e --body "ProdCopy-April2026"
  gh variable set MAILBOX_USER_ID           --repo $repo --env $e --body "invoices@desicongroup.com"
  gh variable set KEYVAULT_SECRETS_OFFICER_OBJECT_IDS --repo $repo --env $e --body "[`"$kvGroup`"]"
  gh variable set ALERT_EMAILS              --repo $repo --env $e --body "<same as dev>"
}
gh variable set AZURE_MIGRATION_CLIENT_ID --repo $repo --env uat --body "<migration identity appId from step 1>"
```

`BC_ENVIRONMENT`: use the same value as dev (check with `gh variable list --env dev`).

## 5. Sign-in URL, role groups, and test users

```powershell
cd ..\..\..\scripts
$url = terraform -chdir=..\terraform\environments\uat output -raw web_app_url
.\bootstrap-entra-app.ps1 -Environment uat -WebAppUrls $url
```

Then go to Entra admin center → **Enterprise applications** → *Desicon VIM Workbench (uat)* → **Users and groups**. Remove the *Application type* filter if the app doesn't show. Assign the Finance testers to their roles:

- **Accounts Payable**
- **Finance Manager**
- **Procurement**: never the same person as Accounts Payable

## 6. Business Central connector (uat)

```powershell
.\bootstrap-bc-connector.ps1 -Environment uat
```

Do the steps it prints:

1. Open the consent link in `desiconerp.com` and accept.
2. In BC (*ProdCopy*), go to **Microsoft Entra Applications → New**: the client ID it printed, **State Enabled**, **D365 READ**, company *Desicon Engineering Ltd*.
3. Run the `gh variable set BC_CLIENT_ID …` line it prints.

## 7. The invoices@ mailbox

```powershell
$func = terraform -chdir=..\terraform\environments\uat output -raw function_app_principal_id
.\grant-mailbox-access.ps1 -FunctionAppPrincipalId $func -MailboxAddress invoices@desicongroup.com -Environment uat
```

This allows the UAT function app to send from **that mailbox only**. It can take up to 2 hours to take effect.

## 8. First pipeline run, then the database users

1. Go to **Actions → Deploy → Run workflow**, set **Last environment = uat**, and leave *Terraform only* unticked. Dev runs first, then UAT.
2. Then create the database users. The SQL firewall already allows your IP from step 3.

```powershell
$o = terraform -chdir=..\terraform\environments\uat output -json | ConvertFrom-Json
sqlcmd -S $o.sql_server_fqdn.value -d $o.sql_database_name.value --authentication-method ActiveDirectoryDefault `
  -i create-sql-users.sql `
  -v WebAppName=$($o.web_app_name.value) FunctionAppName=$($o.function_app_name.value) MigrationIdentityName=sp-vim-migrations-uat
```

3. Re-run **Deploy (uat)**. It should go fully green.

## 9. Vendor portal (uat)

This reuses the **Desicon Vendors** tenant. Details are in [vendor-portal.md](vendor-portal.md).

```powershell
az login --tenant 31a1b00e-9c2a-4457-a4aa-db82ea2673a2 --allow-no-subscriptions
.\bootstrap-portal-app.ps1 -Environment uat
```

1. Run the three printed `gh variable set` lines for both `uat` and `uat-plan`.
2. In Entra admin center (Desicon Vendors), go to **User flows → vendor-signin → Applications → Add**: *Desicon VIM Vendor Portal (uat)*.
3. Run **Deploy (uat)**. The portal smoke test fails the first time; that's expected.
4. Then:

```powershell
az login --tenant 77e479f1-fefd-4238-a1a9-6b1f692f20b8
$p = terraform -chdir=..\terraform\environments\uat output -raw portal_app_name
sqlcmd -S $o.sql_server_fqdn.value -d $o.sql_database_name.value --authentication-method ActiveDirectoryDefault `
  -i create-portal-sql-user.sql -v PortalAppName=$p
az webapp restart -g rg-vim-uat-weu -n $p
.\bootstrap-bc-connector.ps1 -Environment uat      # adds the portal's trust; ignore the "Next…" steps
az login --tenant 31a1b00e-9c2a-4457-a4aa-db82ea2673a2 --allow-no-subscriptions
.\bootstrap-portal-app.ps1 -Environment uat -PortalUrl https://$p.azurewebsites.net
```

5. Run **Deploy (uat)** once more. It should go fully green.

## 10. Clean up and hand over to Finance

- **SQL firewall:** remove your IP from `sql_allowed_ip_addresses` in `uat.auto.tfvars`, then run `terraform apply` once more from this PC. For later one-off SQL jobs, use the temporary-rule commands instead.
- **Desicon Vendors subscription link:** in Entra admin center (Desicon Vendors), go to **External Identities → Linked subscriptions** and link *Azure subscription 1*, resource group `rg-vim-externalid`. This gives the vendor sign-in an SLA. Do it once, before UAT testing with real vendors.
- **Hand over to Finance:** give the testers the workbench URL. Have Procurement invite one or two friendly vendors to the UAT portal.
- **UAT test script for Finance** (each item is a sign-off line):
  1. A vendor submits a materials invoice and a services invoice.
  2. A wrong amount is refused before submission.
  3. A flagged case goes to the Finance Manager.
  4. Accounts accepts, and the vendor sees "Accepted on …".
  5. Accounts returns an invoice, and the vendor corrects it.
  6. Reminders arrive and escalate after one working day.
  7. Procurement removes a vendor user, and that user loses access.

---

## Prod checklist (at go-live)

Same steps 1–10 with `prod` in place of `uat`, plus:

- **Cost:** prod is the expensive one: 2 × P1v3, a provisioned GP_Gen5_2 database, zone-redundant storage and private networking. Start it only at go-live.
- **BC:** environment *Production* (`BC_ENVIRONMENT`); a BC connector `(prod)` with D365 READ.
- **Private networking is on**, so SQL has no public endpoint. Before step 8, set up the **self-hosted runner** on a small VM in the VNet and set `PROD_RUNNER_LABEL` (infrastructure.md §4.11). Run the `sqlcmd` steps from inside the VNet as well.
- **Protect the environment:** **Settings → Environments → prod → Required reviewers**, and deployment branch `main` only.
- **Delete lock:** `az lock create --name vim-prod-no-delete --resource-group rg-vim-prod-weu --lock-type CanNotDelete`
- **Retention:** confirm with Finance before go-live. The prod `variables.tf` starting points are 35-day database backups with 7-year long-term retention, and 90 days of logs.
- **Cut-over:** invite vendors in waves (pilot, then Wave 1 = vendors with open POs, then Wave 2) with about 4 weeks of overlap; then switch email intake off for good.
