# Backlog

## To finish Phase 1 (before UAT)

- [ ] First build on a developer machine; fix compile issues in Infrastructure, Functions and `Program.cs`
- [ ] Generate the initial EF Core migration and a migration bundle (`docs/operations/database.md`)
- [ ] `terraform validate` / `tflint` for each environment; deploy **dev**
- [ ] SHA-pin all GitHub Actions (`npx ratchet pin .github/workflows/*.yml`)
- [ ] Create the Entra app registration and app roles; assign test users per role
- [ ] Grant the Functions identity access to the invoice mailbox only (`infra/scripts/grant-mailbox-access.ps1`)
- [ ] Load real reviewer object ids for the five SOP categories (Invoice routing screen)
- [ ] Enable Defender for Storage malware scanning on the documents account and confirm the scan-result tag name (`BlobScanStatusReader`)
- [ ] One-time: give the deploy identity rights to create the custom "VIM Blob Index Tag Reader" role (see infrastructure.md)
- [ ] Anchor each case's audit head hash outside the database (e.g. immutable blob) so a full-chain rewrite by a DBA is detectable
- [ ] Limit document downloads for Reviewers to cases they were assigned; record downloads as audit events
- [ ] Integration tests against SQL Server in Docker (repository, sequence, concurrency, audit DENY)
- [ ] Accessibility and usability pass with the Document Controller and one Responsible Officer
- [ ] Threat-model workshop; NDPA data protection impact assessment with Legal
- [ ] UAT with Finance using real (anonymised) invoices; Finance sign-off (Gate 2)

## Decisions needed from Finance (design basis §13.3)

- [ ] Admin / Logistics invoices: Logistics Manager (SOP) or Admin Manager?
- [ ] Invoice mailbox domain (desicon-eng.com vs desicongroup.com)
- [ ] "48 hours" for review: calendar or working hours? (default: calendar, `Vim:VerificationBasis`)
- [ ] Confirm checklist wording "does not exceed remaining PO/SO balance" (code: `REV_AMOUNT_WITHIN_ORDER`)
- [ ] Add "Invoice" to the materials list in SOP section 6
- [ ] AP processing target time
- [ ] Retention period for invoice records
- [ ] Lagos public holiday calendar for working-hour deadlines

## Phase 2 — Business Central validation

- [ ] Entra app registration for BC; permission set; enable `BusinessCentral:Enabled`
- [ ] AL extension with a custom API page for vendor bank accounts (not in the standard API)
- [ ] Confirm how GRVs and WCCs are recorded in BC; map receipt checks
- [ ] Line-level matching flags (quantity, price, currency)
- [ ] Power BI report over reporting views

## Phase 3 — Vendor portal and ERP write-back

- [ ] Vendor portal with Entra External ID; strict per-vendor isolation; penetration test
- [ ] Create an unposted purchase invoice in BC from a verified case (idempotent on reference)
- [ ] Payment status sync → `RecordPayment` / `Close` (port `GetAwaitingPaymentAsync` is ready)
