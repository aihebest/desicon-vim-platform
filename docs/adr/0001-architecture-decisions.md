# Architecture Decision Records

Short records of decisions that shape the platform. Each says what was decided, why,
and what it costs. Add a new record rather than editing an old one when a decision changes.

---

## ADR-001 — Separate repository, Blazor workbench

**Status:** Accepted (2026-10-17, ICT Manager)

**Context.** Desicon already has a Finance Workflow Platform (.NET 8, React) with a generic
workflow engine. Building VIM as a module there was considered.

**Decision.** Build VIM as its own repository with a Blazor Server workbench.

**Consequences.** One language (C#) for UI and back end, which suits a small team. VIM
does not share the Finance Workflow Platform's engine, audit store or pipelines, so
common patterns (hash-chained audit, Managed Identity, OIDC CI) are re-implemented here.
Revisit consolidation once both platforms are in production.

## ADR-002 — .NET 10 (LTS)

**Status:** Accepted

**Decision.** Target .NET 10. .NET 8 reaches end of support in November 2026.

**Consequences.** Long support window (to November 2028). The Finance Workflow Platform
should plan the same upgrade.

## ADR-003 — Email first, vendor portal later

**Status:** Accepted

**Decision.** The dedicated mailbox from SOP DEL/FIN/AP/SOP/001 is the Phase 1 intake channel.
The platform reads it through Microsoft Graph, restricted by Exchange RBAC for Applications
to that one mailbox.

**Consequences.** Vendors need no account or training. The vendor portal (Entra External ID)
follows in Phase 3, when vendor isolation and a penetration test can be done properly.

## ADR-004 — Business Central is the system of record; the platform never posts or pays

**Status:** Accepted (confirmed by Finance, 2026-10-17)

**Decision.** The platform reads vendor, order and receipt data to raise flags, and records
the BC document number AP enters. Writing a draft purchase invoice is a Phase 3 option. It
never posts, approves payment or changes vendor bank details.

**Consequences.** No duplicate ledger and no conflicting payment controls. Bank-detail checks
compare the invoice with the vendor master and flag differences; the master is never updated
from an invoice.

## ADR-005 — Workflow rules in code, routing and checklists as configuration

**Status:** Accepted

**Decision.** The status machine and segregation-of-duties rules are code
(`WorkflowRules`, `InvoiceCase`) with tests. Category routing, reviewers, delegates, deadlines
and the checklist content are data that Finance can change.

**Consequences.** Control rules cannot be weakened by a configuration change. Changing a
control needs a code change, a review and a release, which is what auditors expect.

## ADR-006 — Insert-only, hash-chained audit trail

**Status:** Accepted

**Decision.** Every change appends an `AuditEvent` carrying the SHA-256 hash of the previous
event. The application's database identity has `DENY UPDATE, DELETE` on the audit table.

**Consequences.** Removing or altering history is detectable (`AuditEvent.VerifyChain`, shown
on every case page). Corrections are new events, never edits.

## ADR-007 — Outbox for notifications

**Status:** Accepted

**Decision.** Notifications are saved in the same transaction as the case change and sent by
a background function.

**Consequences.** A mail outage never loses a notification or rolls back a decision. Emails
arrive up to a minute after the action.

## ADR-008 — Short-lived DI scope per action in Blazor Server

**Status:** Accepted

**Decision.** Workbench pages create a new DI scope (and database context) for each load or
action (`VimComponentBase.InScope`).

**Consequences.** No stale data from a database context that lives as long as a browser tab.
Two people acting on the same case are protected by the `RowVersion` concurrency check.
