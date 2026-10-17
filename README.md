# Desicon Vendor Invoice Management (VIM) Platform

Electronic receipt, verification and Accounts Payable handoff of vendor invoices for
Desicon Engineering Limited. It implements SOP **DEL/FIN/AP/SOP/001** and the Finance
**Invoice Review Checklist**, and follows the design basis **DEL/ICT/ARC/VIM/001**.

> **Business Central remains the financial system of record.** This platform manages the
> document workflow. It never posts invoices or makes payments.

---

## What it does (Phase 1)

| Step | Who | What the platform does |
|---|---|---|
| Vendor emails invoice package | Vendor | Reads the invoice mailbox, accepts genuine PDFs only, assigns a reference (`VIM-2026-000123`), stores the files, reads key fields from the invoice, and emails an acknowledgement |
| Register | Document Controller | Confirms details and category, answers the intake checklist. The system checks for duplicates and, when enabled, validates against Business Central |
| Verify | Responsible Officer | Gets an email and a 48-hour deadline. Answers the verification checklist, or returns the package to the vendor with a reason |
| Process | AP Coordinator | Accepts verified cases, then records the Business Central purchase invoice number |
| Oversee | Finance Manager | Dashboard, overdue escalations, clears fraud-sensitive flags, maintains routing |

Built-in controls:
- **Segregation of duties:** the registrar cannot verify; AP must be someone other than the registrar, the verifier and anyone who cleared a blocking flag; a case cannot be routed to the person routing it.
- **Blocking flags:** possible duplicates, failed sender authentication, bank-detail mismatches, ERP validation failures and malware all stop AP. Only the Finance Manager can clear a blocking flag.
- **Clean files only:** every document must pass the Defender malware scan before AP can act, and only clean files can be opened.
- **Reassignment** is limited to the reviewer and delegates configured for the category; routing changes are recorded in an insert-only history.
- **Audit trail:** every action is recorded with a SHA-256 hash chain, so a deleted or altered entry is detectable.

## Repository layout

```
src/
  Desicon.Vim.Domain/          Business rules. No packages, no Azure. Fully unit-tested.
  Desicon.Vim.Application/     Use cases (intake, workflow, deadlines) and the interfaces they need.
  Desicon.Vim.Infrastructure/  Azure SQL (EF Core), Blob Storage, Graph, Document Intelligence, Business Central.
  Desicon.Vim.Web/             Blazor workbench for staff (Entra ID sign-in).
  Desicon.Vim.Functions/       Timers: mailbox intake, deadline reminders, sending notifications.
tests/                         xUnit tests for domain and application.
infra/terraform/               Azure infrastructure (dev / uat / prod).
.github/workflows/             CI (build, test, security scans) and deployment (OIDC, no secrets).
docs/                          ADRs, operations guides, backlog.
```

The dependency rule: **Domain ← Application ← Infrastructure / Web / Functions**. The
domain never references anything outward, which is why its rules can be tested in seconds.

## Getting started on Windows

You need: **.NET 10 SDK**, **Docker Desktop**, **VS Code** (with the C# Dev Kit extension), **Git**.

```powershell
# 1. Get the code
git clone https://github.com/aihebest/desicon-vim-platform.git
cd desicon-vim-platform

# 2. Build everything and run the tests
dotnet build
dotnet test
```

To run the workbench locally against Docker SQL Server and Azurite, see
[docs/operations/local-development.md](docs/operations/local-development.md).
Database setup and migrations: [docs/operations/database.md](docs/operations/database.md).
Azure deployment: [docs/operations/infrastructure.md](docs/operations/infrastructure.md).

## Verification status of this first drop

This code was written in an environment without access to NuGet, so only part of it
could be compiled there. **Run `dotnet build` and `dotnet test` on your machine first.**

| Part | Status |
|---|---|
| Domain | Compiled with warnings as errors; **45 tests pass** |
| Application | Compiled with warnings as errors; **21 tests pass** |
| Blazor workbench pages | Compiled with warnings as errors inside a local harness; screens rendered and checked |
| Infrastructure, Functions | Compiled only against hand-written stand-ins for the Azure/EF/Graph packages — **not yet built with the real packages**; expect a few fixes on first build |
| `Program.cs` (Entra sign-in wiring) | **Not yet compiled** |
| Terraform | Formatted, tflint, checkov (71 passed) and policy tests pass; **`terraform validate`/`plan` not yet run** (needs registry access) |
| EF Core migrations | Not generated yet — see `docs/operations/database.md` |

## Contributing rules

1. Every status change goes through a method on `InvoiceCase`. No other code sets `Status`.
2. Business rules live in the Domain project and come with a test.
3. No secrets in the repo, ever. Azure access uses Managed Identity; CI uses OIDC.
4. `main` is protected: changes arrive by pull request with a passing pipeline.

See [docs/backlog.md](docs/backlog.md) for what's next.
