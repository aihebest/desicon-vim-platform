# Local development (Windows, PowerShell, Docker Desktop, VS Code)

This guide gets the VIM platform running on your own machine. Each step says
what it does and why. Commands are for **PowerShell**, run from the
repository root (the folder containing `Desicon.Vim.sln`) unless stated
otherwise.

## What runs where

| Piece | On your machine | In Azure |
|---|---|---|
| Database | SQL Server 2022 in a Docker container | Azure SQL |
| Document storage | **Azurite** (Microsoft's storage emulator) in Docker | Azure Blob Storage |
| Workbench (website) | `dotnet run` of `Desicon.Vim.Web` | App Service |
| Background jobs | Azure Functions Core Tools (`func start`) | Function App |
| Invoice mailbox / email | A **real** test mailbox in a Microsoft 365 tenant (there is no Graph emulator) | The production invoice mailbox |

Locally the apps sign in to SQL and storage with a password or a connection
string. In Azure they use their **Managed Identity** (an identity Azure gives
the app, so no passwords are stored anywhere). Never copy local passwords to
Azure settings.

## 1. Install the tools (once)

```powershell
winget install Microsoft.DotNet.SDK.10          # .NET 10 SDK (to build and run the code)
winget install Microsoft.Azure.FunctionsCoreTools  # 'func' command, to run the background jobs
winget install Microsoft.AzureCLI               # 'az' command, to sign in to Azure
winget install Microsoft.Sqlcmd                 # optional: query SQL from the command line
dotnet tool install --global dotnet-ef --version "10.0.*"   # EF Core migrations tool
```

Close and reopen PowerShell, then check:

```powershell
dotnet --version     # should start with 10.
func --version       # should start with 4.
```

Docker Desktop must be running (whale icon in the system tray). In VS Code,
install the **C# Dev Kit** and **Azure Functions** extensions.

## 2. Start SQL Server and Azurite

Create a `.env` file holding the SQL Server administrator password. `.env` is
git-ignored, so the password never reaches GitHub. Pick your own strong
password (at least 8 characters with upper case, lower case, a digit and a
symbol; SQL Server refuses weak ones and the container then stops).

```powershell
Set-Content -Path .env -Value "MSSQL_SA_PASSWORD=<choose a strong password>"
docker compose up -d
docker compose ps        # both containers should show "running"; sqlserver becomes "healthy" after ~30 s
```

If `vim-sqlserver` keeps restarting, run `docker logs vim-sqlserver`; the
usual cause is a password that is too weak.

## 3. Give the workbench its local settings (user-secrets)

**User-secrets** store settings for one project in your Windows user profile,
outside the repository, so they cannot be committed by accident.

```powershell
$sa = "<the same password you put in .env>"
dotnet user-secrets --project src/Desicon.Vim.Web set "ConnectionStrings:VimDb" "Server=localhost,1433;Database=VimDev;User Id=sa;Password=$sa;TrustServerCertificate=True;Encrypt=True"
dotnet user-secrets --project src/Desicon.Vim.Web set "Storage:ConnectionString" "UseDevelopmentStorage=true"
```

- `TrustServerCertificate=True` is acceptable only for the local container,
  which uses a self-signed certificate. Azure connection strings never have it.
- `Storage:ConnectionString` makes the app use Azurite instead of a real
  storage account. The documents container (`case-documents`) is created
  automatically in Azurite on the first upload.

Optional, so that new cases can be routed to reviewers: set the Entra object
id of a test user for each category (find it in Entra admin center > Users >
the user > Object ID):

```powershell
dotnet user-secrets --project src/Desicon.Vim.Web set "Vim:Routes:PMC:ReviewerId" "<entra object id>"
```

Routes are created only when the `CategoryRoutes` table is empty (first
start). Categories without a configured reviewer are skipped with a warning
in the log, and can be added later in the workbench.

## 4. Create the database tables

```powershell
dotnet build
dotnet ef database update -p src/Desicon.Vim.Infrastructure -s src/Desicon.Vim.Web `
  --connection "Server=localhost,1433;Database=VimDev;User Id=sa;Password=$sa;TrustServerCertificate=True;Encrypt=True"
```

If the repository has no `Persistence/Migrations` folder yet, create the
first migration before this step; see [database.md](database.md).

## 5. Run the workbench

```powershell
dotnet run --project src/Desicon.Vim.Web
```

The console prints the address (for example `https://localhost:5001`). Open
it in your browser; you sign in with your Desicon Microsoft account. Stop the
app with `Ctrl+C`.

The first time, .NET may ask you to trust its local HTTPS certificate:
`dotnet dev-certs https --trust`.

## 6. Run the background jobs (Azure Functions) locally

```powershell
Copy-Item src/Desicon.Vim.Functions/local.settings.json.example src/Desicon.Vim.Functions/local.settings.json
code src/Desicon.Vim.Functions/local.settings.json
```

Edit `local.settings.json` (it is git-ignored):

- Put your SA password into `ConnectionStrings:VimDb`.
- Leave `AzureWebJobsStorage` and `Storage:ConnectionString` as
  `UseDevelopmentStorage=true` (Azurite).
- Set `Mailbox:UserId` to your **test** mailbox (see step 7), or leave the
  placeholder: intake and email sending then log a warning and do nothing.

Then:

```powershell
cd src/Desicon.Vim.Functions
func start
```

You should see four functions listed: `MailboxIntake` (every 5 minutes),
`SlaMonitor` (every 15 minutes), `OutboxDispatch` (every minute) and
`ScanResultSync` (every minute). `ScanResultSync` copies malware-scan results
from Microsoft Defender for Storage onto documents. Azurite has no Defender,
so locally every document stays at scan status **Pending**; that is expected. To run
one immediately instead of waiting for its timer, call the admin endpoint
from a second PowerShell window:

```powershell
Invoke-RestMethod -Method Post -Uri "http://localhost:7071/admin/functions/MailboxIntake" -ContentType "application/json" -Body '{}'
```

In Azure, the same settings use double underscores instead of colons
(`Mailbox__UserId`), because App Service does not allow `:` in names.

## 7. Testing email intake without the production mailbox

Mailbox intake and notification emails use **Microsoft Graph**, the API for
Microsoft 365. Graph only exists in a real tenant: there is no local
emulator. Do **not** point a development machine at the production invoice
mailbox. Instead:

1. Ask the Microsoft 365 administrator for a **dedicated test shared
   mailbox** (e.g. `vim-dev-invoices@example.com`), or use a free
   Microsoft 365 developer tenant.
2. Create a **development app registration** in Entra ID with the Graph
   *application* permissions `Mail.ReadWrite` and `Mail.Send`, limited to the
   test mailbox only (Exchange "RBAC for Applications", the same approach as
   `infra/scripts/grant-mailbox-access.ps1`). Create a client secret for it.
3. Give the Functions host that identity through the standard Azure SDK
   environment variables, by adding these to `Values` in `local.settings.json`:

   ```json
   "AZURE_TENANT_ID": "<tenant id>",
   "AZURE_CLIENT_ID": "<dev app registration client id>",
   "AZURE_CLIENT_SECRET": "<dev client secret>"
   ```

   `DefaultAzureCredential` picks these up automatically on your machine. In
   Azure they are never set; the managed identity is used instead.
   (Signing in with `az login` is not enough for Graph mail: the Azure CLI
   token does not carry mail permissions.)
4. Send an email with a PDF attachment to the test mailbox, then trigger
   `MailboxIntake` (step 6). Expected result: a new case in the workbench, the
   PDF in Azurite (browse it with **Azure Storage Explorer** > Local &
   Attached > Emulator), the email moved to the mailbox's `Processed` folder,
   and an acknowledgement in `vim.OutboxMessages` that `OutboxDispatch` sends.
5. Send an email without a PDF to see the "PDF required" reply, and reply to
   a returned case keeping `VIM-yyyy-nnnnnn` in the subject to test a
   resubmission.

The intake logic itself (PDF checks, duplicate detection, resubmissions) is
covered by unit tests with fake mailboxes, so you can work on it without any
tenant: `dotnet test`.

## Everyday commands

```powershell
docker compose up -d          # start SQL Server + Azurite
docker compose down           # stop them (data is kept)
docker compose down -v        # stop and DELETE all local data (fresh start)
dotnet build                  # compile everything
dotnet test                   # run the tests
```

## Troubleshooting

| Symptom | Likely cause and fix |
|---|---|
| `A network-related or instance-specific error` | SQL container not running or not healthy yet: `docker compose ps`, wait 30 s. |
| `Login failed for user 'sa'` | Password in user-secrets / local.settings.json differs from `.env`. |
| `ConnectionStrings:VimDb is not configured` | Step 3 (web) or step 6 (functions) was skipped. |
| `Invalid object name 'vim.Cases'` | Tables not created yet: step 4. |
| Functions fail with `'%MailboxIntakeSchedule%' does not resolve` | `local.settings.json` is missing the schedule settings; recopy the example. |
| Azurite errors about API version | Make sure the container was started from `docker-compose.yml` (it passes `--skipApiVersionCheck`). |
