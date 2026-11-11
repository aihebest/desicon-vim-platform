# ADR 0003: Production starts lean, on public endpoints protected by sign-in

- **Status:** accepted, 11 November 2026
- **Decided by:** ICT (DevSecOps), after Finance signed off UAT
- **Supersedes:** the production sizing in ADR 0001 (full specification from day one)

## Decision

Production launches at a small size and is scaled up when usage justifies it:

| | Lean start (now) | Full specification (later) |
|---|---|---|
| App Service plan | 1 × Basic **B2** (workbench, vendor portal, functions) | 2 × Premium P1v3 |
| Database | Serverless `GP_S_Gen5_1`, auto-pause off | Provisioned `GP_Gen5_2` |
| Network | **Public endpoints**, Entra-only sign-in | Private endpoints in a VNet |
| Deployments | GitHub-hosted runners | Self-hosted runner inside the VNet |

Kept at production strength from day one: 35-day database backups with 7-year long-term retention, geo-redundant backups, zone-redundant document storage, malware scanning on every upload, 90-day logs, the insert-only audit trail.

## Why

- Invoice volumes are not yet known; the full specification costs several hundred dollars a month before the first vendor invoice arrives.
- Private networking needs a self-hosted deployment runner (a VM to patch and monitor) for a one-person platform team.
- Nothing has to be rebuilt to scale up: the changes are variable defaults in `infra/terraform/environments/prod/variables.tf`.

## The risk accepted

In prod, SQL, Storage, Key Vault and Document Intelligence accept connections from the internet instead of only from a private network. The policy gate (`infra/policy/terraform/vim_security.rego`, rule 7) normally refuses this; it is allowed only because `accept_public_data_endpoints = true` is recorded in the prod root.

Compensating controls (all enforced by the same policy gate and unchanged):

- **No passwords or keys anywhere.** SQL is Entra-only; storage has shared keys disabled; Document Intelligence has local auth disabled. A stolen connection string is useless.
- **SQL firewall is deny-by-default**: only Azure services and addresses listed in `sql_allowed_ip_addresses` (normally none) get as far as the sign-in.
- TLS 1.2+, no anonymous blob access, RBAC-only Key Vault, Defender malware scanning, diagnostics to Log Analytics, alerts on failures.
- The apps' identities hold least-privilege roles; the vendor portal has its own narrow database role.

Residual risk: a flaw in Microsoft's identity layer, or a leaked Entra credential with data rights, is reachable from anywhere rather than only from inside the VNet.

## When to move to the full specification

Any one of:

- Finance or an auditor requires network isolation for financial records.
- More than about 50 staff/vendor users active in a day, or the B2 server shows sustained memory pressure or slow pages.
- The platform takes on bank-detail changes or payment instructions.

Then set, in the prod root: `app_service_sku_name = "P1v3"`, `app_service_worker_count = 2`, `sql_sku_name = "GP_Gen5_2"`, `enable_private_networking = true`, `accept_public_data_endpoints = false`, and follow `docs/operations/infrastructure.md` §4.11 for the self-hosted runner.
