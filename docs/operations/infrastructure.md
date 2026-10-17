# VIM Platform — Infrastructure and Deployment Guide

**Audience:** the engineer who runs the platform's Azure infrastructure. You know Terraform; this guide explains the application side as it goes, and every command is given for **Windows PowerShell**.

**What lives where**

| Path | What it is |
|---|---|
| `infra/terraform/modules/` | Reusable building blocks (one folder per concern) |
| `infra/terraform/environments/{dev,uat,prod}/` | One Terraform root per environment. `main.tf` is identical in all three; sizes differ in `variables.tf` |
| `infra/policy/terraform/` | OPA/conftest rules every plan must pass (plus unit tests) |
| `infra/scripts/` | One-time bootstrap scripts (PowerShell) and the SQL users script |
| `.github/workflows/ci.yml` | Checks on every pull request (no Azure access) |
| `.github/workflows/deploy.yml` (+ `deploy-environment.yml`) | Build once, deploy dev → uat → prod |
| `.config/tflint.hcl`, `.config/gitleaks.toml` | Linter and secret-scanner settings |

---

## 1. The big picture

```
   Staff browser ──HTTPS + Entra sign-in──►  Web app (Blazor)  ─┐
                                                                ├─► Azure SQL (cases, workflow, audit)
   Invoice mailbox ◄──Graph (scoped)──  Function app (timers) ──┤
                                                                ├─► Blob storage (case documents, malware-scanned)
                                                                ├─► Document Intelligence (reads invoices)
                                                                ├─► Key Vault (Business Central secret)
                                                                └─► Business Central API
            Both apps ──logs/metrics──► Application Insights ──► Log Analytics ◄── every resource's diagnostics
```

**No passwords anywhere.** Each app has a *managed identity* — an identity Azure creates for the app and manages for it, with no password anyone can leak. Instead of connection strings with keys, each service is told “this identity may do X” (an Azure *role assignment*). The one secret we can’t avoid — Business Central’s API client secret — sits in Key Vault, and the apps read it with their identity.

---

## 2. What each resource is for

| Resource (example dev name) | Why it exists | Module |
|---|---|---|
| Resource group `rg-vim-dev-weu` | Container for everything in one environment | env root |
| Log Analytics `law-vim-dev-weu` | Central log store. Every resource sends diagnostics here, so one query can follow an incident across services | `monitoring` |
| Application Insights `appi-vim-dev-weu` | Application telemetry (requests, errors, dependencies, traces) from both apps; stored in the workspace above | `monitoring` |
| Key Vault `kv-vim-dev-weu-xxxx` | Holds `bc-client-secret`. RBAC mode, purge protection, 90-day soft delete | `keyvault` |
| Storage `stvimdevweuxxxx` | `case-documents` container: invoices, delivery notes, GRVs. No shared keys, no public blobs, versioning, 30-day soft delete, Defender malware scanning on upload, optional WORM | `storage` |
| Azure SQL server + `VimDb` | Transactional data and the insert-only audit table. Entra-only auth (no SQL logins), TLS 1.2, auditing to Log Analytics | `sql` |
| Document Intelligence `di-vim-dev-weu-xxxx` | Extracts invoice fields (vendor, number, totals, lines). API keys disabled — identity only | `docintel` |
| App Service plan `asp-vim-dev-weu` | The compute both apps run on (one bill for both) | `app-hosting` |
| Web app `app-vim-dev-weu-xxxx` | `Desicon.Vim.Web` — the staff workbench (.NET 10, Linux) | `app-hosting` |
| Function app `func-vim-dev-weu-xxxx` | `Desicon.Vim.Functions` — mailbox intake (5 min), SLA escalation (15 min), notification outbox (1 min) | `app-hosting` |
| Host storage `stfnvimdevweuxxxx` | Internal bookkeeping for the Functions runtime (timer locks). Kept apart from documents | `app-hosting` |
| VNet, private endpoints, private DNS (prod) | Puts SQL, Storage, Key Vault and Document Intelligence on private addresses and switches their public endpoints off | `network` |
| Role assignments | Who may do what (see table below) | `role-assignments` |

**Role assignments created by Terraform**

| Identity | Role | On |
|---|---|---|
| Web + Function | Storage Blob Data Contributor | documents storage |
| Web + Function | Cognitive Services User | Document Intelligence |
| Web + Function | Key Vault Secrets User | Key Vault |
| Function | Storage Blob Data Owner, Queue Data Contributor, Table Data Contributor | host storage |
| Your admin group | Key Vault Secrets Officer | Key Vault (to set `bc-client-secret`) |

**Naming:** `<type>-vim-<env>-<region>`, e.g. `rg-vim-dev-weu`. Names that must be unique across all of Azure get a random 4-character suffix (stored in state, so it never changes). **Tags** on everything: `application=vim`, `owner=ICT`, `business_owner=Finance`, `environment`, `managed_by=terraform`.

**Region:** default `westeurope` because every service used is available there. **South Africa North** is closer to Port Harcourt; before switching, confirm Document Intelligence (prebuilt invoice model), Defender for Storage malware scanning and SQL serverless are available there, and whether Desicon has a data-residency preference. Changing region later means rebuilding everything.

### Environment differences

| Setting | dev | uat | prod |
|---|---|---|---|
| Private networking | off | off | **on** |
| App Service plan | B1 × 1 | B1 × 1 | P1v3 × 2 |
| SQL | GP serverless 1 vCore, auto-pause 60 min | same | GP provisioned 2 vCores, no auto-pause |
| SQL backups | 7-day PITR, local | same | 35-day PITR, geo, LTR (weekly 5w / monthly 12m / yearly 7y — **confirm with Finance**) |
| Documents storage | LRS | LRS | ZRS |
| Log retention / daily cap | 30 d / 1 GB | 30 d / 2 GB | 90 d / none |
| Business Central | Sandbox | Sandbox | Production |
| `ASPNETCORE_ENVIRONMENT` | `Dev` | `Uat` | `Production` |

With private networking **off**, data services have public endpoints but still accept **only Entra-authenticated identities with a role** — no keys exist to steal. The SQL server allows “Azure services” (needed because the apps aren’t in a VNet) and requires Entra login.

---

## 3. Tools to install once (PowerShell, as yourself)

```powershell
winget install Microsoft.AzureCLI
winget install Hashicorp.Terraform
winget install GitHub.cli
winget install sqlcmd                       # go-sqlcmd: supports Entra sign-in
Install-Module ExchangeOnlineManagement -Scope CurrentUser
```

Close and reopen PowerShell afterwards so the new commands are on your PATH.

---

## 4. One-time bootstrap (in this order)

Each step says **why** it comes where it does. Do dev end-to-end first; uat and prod repeat steps 3–10 (see 4.11).

### 4.0 Sign in and choose the subscription

```powershell
az login
az account set --subscription "<subscription name or ID>"
az account show --query "{name:name, id:id, tenant:tenantId}" -o table
```

### 4.1 Terraform state storage

Terraform needs somewhere to keep its state before it can build anything.

```powershell
cd infra\scripts
.\bootstrap-state.ps1                  # prints the storage account name; note it
```

### 4.2 Entra security groups

Groups, not individuals, hold admin rights — so access changes by group membership, never by editing code.

```powershell
az ad group create --display-name "grp-vim-sql-admins-dev"      --mail-nickname "grp-vim-sql-admins-dev"
az ad group create --display-name "grp-vim-kv-secrets-dev"      --mail-nickname "grp-vim-kv-secrets-dev"
# Workbench role groups (assign people to these, not to the app directly):
foreach ($r in "documentcontrollers","reviewers","accountspayable","financemanagers","platformadmins","auditors") {
  az ad group create --display-name "grp-vim-$r" --mail-nickname "grp-vim-$r"
}
# Add yourself to the admin groups:
$me = az ad signed-in-user show --query id -o tsv
az ad group member add --group "grp-vim-sql-admins-dev" --member-id $me
az ad group member add --group "grp-vim-kv-secrets-dev" --member-id $me
# Object IDs you will need:
az ad group show --group "grp-vim-sql-admins-dev" --query id -o tsv
az ad group show --group "grp-vim-kv-secrets-dev" --query id -o tsv
```

### 4.3 CI identities (GitHub OIDC)

Creates `sp-vim-deploy-dev` (runs Terraform, deploys) and `sp-vim-migrations-dev` (runs database migrations only), trusted only for this repository’s `dev-plan`/`dev` GitHub environments. No secrets are created.

```powershell
.\bootstrap-github-oidc.ps1 -Environment dev -GitHubRepository aihebest/desicon-vim-platform `
    -StateStorageAccountName <name from 4.1>
```

Then create the GitHub environments and variables (the script prints the IDs):

```powershell
$repo = "aihebest/desicon-vim-platform"
gh api -X PUT "repos/$repo/environments/dev-plan" | Out-Null
gh api -X PUT "repos/$repo/environments/dev"      | Out-Null

foreach ($env in "dev-plan","dev") {
  gh variable set AZURE_TENANT_ID           --repo $repo --env $env --body "<tenant id>"
  gh variable set AZURE_SUBSCRIPTION_ID     --repo $repo --env $env --body "<subscription id>"
  gh variable set AZURE_CLIENT_ID           --repo $repo --env $env --body "<sp-vim-deploy-dev appId>"
  gh variable set TF_STATE_RESOURCE_GROUP   --repo $repo --env $env --body "rg-vim-tfstate"
  gh variable set TF_STATE_STORAGE_ACCOUNT  --repo $repo --env $env --body "<state account>"
  gh variable set SQL_ADMIN_GROUP_NAME      --repo $repo --env $env --body "grp-vim-sql-admins-dev"
  gh variable set SQL_ADMIN_GROUP_OBJECT_ID --repo $repo --env $env --body "<group object id>"
  gh variable set ENTRA_WORKBENCH_CLIENT_ID --repo $repo --env $env --body "<from step 4.4>"
  gh variable set BC_TENANT_ID              --repo $repo --env $env --body "<tenant id>"
  gh variable set BC_ENVIRONMENT            --repo $repo --env $env --body "Sandbox"
  gh variable set BC_COMPANY_ID             --repo $repo --env $env --body "<BC company GUID>"
  gh variable set BC_CLIENT_ID              --repo $repo --env $env --body "<BC API app client id>"
  gh variable set MAILBOX_USER_ID           --repo $repo --env $env --body "invoices-test@desicongroup.com"
  gh variable set KEYVAULT_SECRETS_OFFICER_OBJECT_IDS --repo $repo --env $env --body '["<grp-vim-kv-secrets-dev id>"]'
}
gh variable set AZURE_MIGRATION_CLIENT_ID --repo $repo --env dev --body "<sp-vim-migrations-dev appId>"
```

These are **variables, not secrets**: IDs are not credentials. The only things that grant access are the federated credentials, which work only for this repository and environment.

### 4.4 Workbench app registration (staff sign-in)

Terraform needs the app’s client ID as an input, so this comes before `terraform apply`.

```powershell
.\bootstrap-entra-app.ps1 -Environment dev -LocalDevUrls https://localhost:5001
```

Copy the printed **Client ID** into `entra_workbench_client_id` (4.5) and the `ENTRA_WORKBENCH_CLIENT_ID` GitHub variable.

### 4.5 First `terraform apply` (dev, from your PC)

```powershell
cd ..\terraform\environments\dev
Copy-Item backend.hcl.example backend.hcl                    # edit: storage_account_name
Copy-Item example.auto.tfvars.example dev.auto.tfvars        # edit: all the IDs
terraform init -backend-config=backend.hcl
terraform providers lock -platform=windows_amd64 -platform=linux_amd64
terraform plan -out tfplan
terraform apply tfplan
terraform output
```

**Commit `.terraform.lock.hcl`** (in each environment folder). It records the exact provider versions and checksums so CI and your PC use identical providers. `backend.hcl` and `*.auto.tfvars` are gitignored.

Optional local policy check against your real plan (needs `conftest.exe` from https://github.com/open-policy-agent/conftest/releases on your PATH):

```powershell
terraform show -json tfplan | Out-File -Encoding utf8 plan.json
conftest test --policy ..\..\..\policy\terraform --namespace terraform.vim.security plan.json
```

### 4.6 Add the real sign-in URL to the app registration

```powershell
cd ..\..\..\scripts
$url = terraform -chdir=..\terraform\environments\dev output -raw web_app_url
.\bootstrap-entra-app.ps1 -Environment dev -WebAppUrls $url -LocalDevUrls https://localhost:5001 `
    -RoleGroups @{ 'Vim.PlatformAdmin' = '<grp-vim-platformadmins id>'; 'Vim.Auditor' = '<grp-vim-auditors id>' }
```

Assign the other role groups the same way. Only people in an assigned group can sign in.

### 4.7 Grant mailbox access (Exchange Online)

The function app reads the invoice mailbox through Microsoft Graph. Granted normally, Graph mail permissions cover **every mailbox in Desicon**; this script grants them through Exchange “RBAC for Applications”, **scoped to the invoice mailbox only**. Do **not** also add Mail.* permissions to this identity in Entra — that would silently undo the scoping (the script warns if it finds any).

```powershell
$func = terraform -chdir=..\terraform\environments\dev output -raw function_app_principal_id
.\grant-mailbox-access.ps1 -FunctionAppPrincipalId $func -MailboxAddress invoices-test@desicongroup.com `
    -Environment dev -TestMailboxAddress <some other mailbox>
```

Permissions can take up to ~2 hours to reach Graph.

### 4.8 Set the Business Central secret

Terraform deliberately does **not** create this secret: anything Terraform writes is stored in plain text in the state file. You set it once, by hand:

```powershell
$kv = terraform -chdir=..\terraform\environments\dev output -raw key_vault_name
$secure = Read-Host "Business Central client secret" -AsSecureString
$plain = [Runtime.InteropServices.Marshal]::PtrToStringBSTR([Runtime.InteropServices.Marshal]::SecureStringToBSTR($secure))
az keyvault secret set --vault-name $kv --name bc-client-secret --value $plain --output none
Remove-Variable plain, secure
```

The apps reference it as `@Microsoft.KeyVault(SecretUri=https://<vault>.vault.azure.net/secrets/bc-client-secret)` — no version, so a rotated secret is picked up within ~24 h or on app restart. In prod (no public vault endpoint) run this from a machine inside the VNet.

### 4.9 Create the database users (first run)

```powershell
$o = terraform -chdir=..\terraform\environments\dev output -json | ConvertFrom-Json
sqlcmd -S $o.sql_server_fqdn.value -d $o.sql_database_name.value --authentication-method ActiveDirectoryDefault `
  -i create-sql-users.sql `
  -v WebAppName=$($o.web_app_name.value) FunctionAppName=$($o.function_app_name.value) MigrationIdentityName=sp-vim-migrations-dev
```

If the connection is refused, add your public IP (`Invoke-RestMethod https://api.ipify.org`) to `sql_allowed_ip_addresses` in `dev.auto.tfvars` and apply. The first run reports that `[vim].[AuditEvents]` doesn’t exist yet — expected.

### 4.10 First pipeline run, then the second SQL run

Push to `main` (or **Actions → Deploy → Run workflow**, `stop_after = dev`). The pipeline applies Terraform, runs migrations (creating the tables), deploys both apps and checks `/health`. Then run 4.9 again — this time it applies `DENY UPDATE, DELETE` on the audit table.

### 4.11 uat and prod

For each: repeat 4.2 (env-named groups), 4.3, 4.4, then **Actions → Deploy → Run workflow** with `infra_only = true` and `stop_after` = that environment (creates the infrastructure only), then 4.6 – 4.10.

**prod specifics**

- Protect the environment: **Settings → Environments → prod → Required reviewers** (add the approvers; tick *Prevent self-review* once there are two people) and **Deployment branches → Selected branches → `main`**. Do the same for `uat` if desired. Leave `*-plan` environments unprotected so plans run without waiting and reviewers can read them.
- Private networking is on, so SQL has no public endpoint. Migrations (and 4.8/4.9) must run **inside the VNet**: register a Linux x64 **self-hosted GitHub runner** on a small VM in (or peered to) the VNet and set the repository variable `PROD_RUNNER_LABEL` to its label. Until then the prod migration step cannot reach SQL.
- Add a delete lock to the resource group:
  `az lock create --name vim-prod-no-delete --resource-group rg-vim-prod-weu --lock-type CanNotDelete`

---

## 5. How a deployment works

1. **build** — publishes the web app, the function app and an EF Core **migration bundle** (a single executable containing every migration) once.
2. Per environment: **plan** (no approval) runs `terraform plan`, checks it with the OPA policy, and prints it in the job summary.
3. **deploy** (waits for approval in prod) applies *exactly* that saved plan, then:
   - opens a temporary SQL firewall rule for the runner (dev/uat), signs in as the **migration identity** (no Azure roles; only `db_ddladmin` in the database) and runs the bundle, then removes the rule;
   - signs back in as the deploy identity and deploys both apps using Entra tokens (FTP/basic-auth publishing is disabled);
   - smoke-tests `GET /health` (which also checks the database) and that the function app is running.

**Why a separate migration identity?** The running apps can read and write data but cannot change the schema or alter the audit table. Only the pipeline’s migration step can, and only during a deployment.

### Pin GitHub Actions to SHAs (do this once, then let Dependabot maintain it)

The workflows reference actions by tag (`@v4`). A tag can be moved by the action’s owner — or by someone who compromises them — so pin each to a commit SHA. These files were written without internet access, so the SHAs could not be filled in:

```powershell
npx --yes ratchet pin .github/workflows/ci.yml .github/workflows/deploy.yml .github/workflows/deploy-environment.yml
git diff        # each uses: line becomes  owner/action@<sha> # ratchet:owner/action@v4
```

Commit the result. Dependabot (`.github/dependabot.yml`) then opens weekly PRs that move both the SHA and the version comment.

### Code scanning on a private repository

CodeQL, SARIF upload and dependency review are free on public repositories but need **GitHub Code Security** (paid) on private ones. Those jobs only run when the repository is public or the repository variable `CODE_SCANNING_ENABLED` is `true`. Everything else in CI (build, tests, gitleaks, Terraform checks, OPA tests, checkov) runs either way.

---

## 6. Day-2 operations

**Rotate the Business Central secret:** create a new secret in the BC app registration, repeat 4.8, restart both apps (`az webapp restart -g <rg> -n <web>` / `az functionapp restart ...`), then delete the old BC secret.

**Logs:** Azure portal → Application Insights → *Logs*, or Log Analytics. Useful queries:

```kusto
// Failed requests in the last hour
requests | where timestamp > ago(1h) and success == false | summarize count() by name, resultCode

// Function runs and failures
traces | where cloud_RoleName startswith "func-vim" | where severityLevel >= 3 | take 50

// Who read secrets from Key Vault
AzureDiagnostics | where ResourceProvider == "MICROSOFT.KEYVAULT" and OperationName == "SecretGet"
| project TimeGenerated, identity_claim_oid_g, CallerIPAddress, ResultSignature

// SQL audit: failed logins
AzureDiagnostics | where Category == "SQLSecurityAuditEvents" and succeeded_s == "false"

// Malware scan results -- only if Defender scan results are sent to this
// workspace (Defender for Storage settings); otherwise read the blob index tag
StorageMalwareScanningResults | take 50
```

**Immutability (WORM) for documents:** off by default. Agree a retention period with Finance/Legal, then set `documents_immutability_mode` (`container` or `account`) and `documents_immutability_period_in_days`. `account` mode on an existing environment **replaces the storage account**. `documents_immutability_locked = true` is **irreversible**.

**After a migration that recreates `[vim].[AuditEvents]`:** re-run `create-sql-users.sql` (dropping a table drops its permissions).

---

## 7. Cost notes

No prices are quoted here — they vary by region, currency and agreement and change over time. Use the **Azure Pricing Calculator** with your region and the SKUs below, and set a **budget alert** on each resource group (*Cost Management → Budgets*).

| Resource | How it is billed | Dev choice / tip |
|---|---|---|
| App Service plan | **Fixed monthly** per instance, whether busy or idle | **B1** is the cheapest tier with always-on and VNet integration (what dev/uat use). **P0v3** is the smallest Premium v3: more memory, deployment slots and zone support. Prod uses P1v3 × 2. One plan serves both apps. |
| Azure SQL | **Serverless:** per vCore-second while running + storage; **auto-pause** stops compute billing when idle (first query after a pause takes up to a minute). **Provisioned:** fixed hourly. | Dev/uat: General Purpose serverless `GP_S_Gen5_1`, auto-pause 60 min. LTR backups add storage cost. |
| Document Intelligence | **Consumption:** per page analysed | **S0** (the F0 free tier has a small monthly page allowance and no private endpoints). |
| Storage | Per GB stored + per transaction; versioning and soft delete keep extra copies | LRS in dev. |
| Defender for Storage | Per storage account per month **plus** per GB scanned for malware | `malware_scanning_cap_gb_per_month` caps the scanning part (10 GB in dev). Can be turned off in dev with `defender_for_storage_enabled = false`. |
| Log Analytics / App Insights | **Consumption:** per GB ingested; retention beyond the included period | Daily cap 1 GB in dev. |
| Key Vault | Per 10,000 operations (small) | — |
| Private endpoints / Private DNS (prod) | Per endpoint per hour + per GB; per DNS zone per month | 7 endpoints, 6 zones in prod. |
| Self-hosted runner VM (prod) | Per VM hour | A small B-series Linux VM is enough; can be shut down between deployments. |

---

## 8. Contract with the application code

The app developers must keep these in step with the infrastructure:

- **App settings** (set by Terraform; `__` maps to `:` in .NET configuration):
  `ConnectionStrings:VimDb`, `Storage:BlobServiceUri`, `Storage:DocumentsContainer` (`case-documents`), `DocumentIntelligence:Endpoint`, `BusinessCentral:{TenantId,Environment,CompanyId,ClientId,ClientSecret}`, `APPLICATIONINSIGHTS_CONNECTION_STRING`; web only `AzureAd:{Instance,TenantId,ClientId}`; functions only `Mailbox:UserId`, `Mailbox:ProcessedFolderName` (`Processed`), `AzureWebJobsStorage__accountName`, `FUNCTIONS_WORKER_RUNTIME=dotnet-isolated`.
- `ASPNETCORE_ENVIRONMENT` / `AZURE_FUNCTIONS_ENVIRONMENT` are `Dev`, `Uat`, `Production` (so `appsettings.Dev.json` etc.; `IsDevelopment()` is false in Azure).
- **Managed identity everywhere:** use `DefaultAzureCredential` (or `ManagedIdentityCredential`) for Blob, Document Intelligence and Graph. Shared keys and Document Intelligence API keys are disabled.
- **Document Intelligence** has outbound access restricted: send document **bytes** (stream/base64), not a blob URL.
- **`/health`** must stay anonymous and cheap; the pipeline smoke test and App Service health check use it.
- **Blazor Server:** ARR affinity and WebSockets are enabled for SignalR circuits.
- **Sign-in only, no secret:** the app registration issues ID tokens (`--enable-id-token-issuance`); the redirect path is `/signin-oidc`.
- **Migrations:** the pipeline builds `dotnet ef migrations bundle --project/--startup-project src/Desicon.Vim.Infrastructure --context VimDbContext`. The apps must **not** run `Database.Migrate()` at startup (they have no DDL rights).
- **Audit table** `[vim].[AuditEvents]`: the app can only INSERT and SELECT.
- **Uploaded documents** carry a Defender scan result in blob index tags (`Malware Scanning scan result`); don’t release a document to reviewers until it reads `No threats found`.

---

## 9. Troubleshooting

| Symptom | Likely cause |
|---|---|
| App setting shows the literal `@Microsoft.KeyVault(...)` text | Secret not set yet (4.8), or the role assignment hasn’t propagated (wait ~10 min, restart) |
| `/health` fails right after first deploy | Database users not created (4.9) or migrations failed — check the deploy log |
| Function app logs storage 403 just after first apply | Role assignments still propagating; it recovers on its own |
| Mail intake: `ErrorAccessDenied` from Graph | 4.7 not run, or still propagating (up to 2 h). Check with `Test-ServicePrincipalAuthorization` |
| `KeyBasedAuthenticationNotPermitted` | Something tried to use a storage key. Keys are disabled by design — use the identity |
| Terraform: `saved plan is stale` | Something changed between plan and apply. Re-run the workflow |
| Terraform: `AuthorizationFailed` creating a role assignment | A module assigns a role not in the allow-list of the deploy identity’s RBAC condition — add it in `bootstrap-github-oidc.ps1` and re-run it |
| Policy gate fails on a plan | Read the message; it names the resource and the rule in `infra/policy/terraform/vim_security.rego` |


## Custom role for malware scan results

Defender for Storage writes each file's scan result as a blob index tag. The
Function App reads that tag to mark documents Clean or Malicious. No built-in
role grants *only* tag read (Storage Blob Data Owner grants far more), so
Terraform creates a custom role, **VIM Blob Index Tag Reader**, scoped to the
documents storage account, and assigns it to the Function App.

Creating a role definition needs `Microsoft.Authorization/roleDefinitions/write`,
which the deploy identity does not have by default. Do one of these once per
environment:

- Run the first `terraform apply` for the environment as an Owner (or User Access
  Administrator) from your own machine, or
- Grant the deploy identity a custom role containing
  `Microsoft.Authorization/roleDefinitions/write` on the resource group, and add the
  new role's id to the condition on its RBAC Administrator assignment
  (`infra/scripts/bootstrap-github-oidc.ps1`).

Also confirm in the Azure portal that **Malware scanning on upload** is enabled for
the documents account and that results are written to index tags.
