# ADR 0002 — Workflow v2: verify first, vendor submits, system checks, Accounts accepts

Status: accepted (stakeholder meeting with the DMD, Finance, Procurement and Project Control, October 2026)

## Decision

| # | Step | Who | In VIM |
|---|------|-----|--------|
| 1 | Goods delivered / service completed | Vendor | — |
| 2 | Delivery verified: GRV / TRV posted in Business Central | Department (Responsible Officer) | Outside VIM for now; VIM reads the posted receipt |
| 3 | Invoice submitted with mandatory document slots | Vendor on the portal, or Accounts Payable for vendors who cannot (assisted) | `/submit`, `SubmissionService` |
| 4 | Automatic checks: virus scan, slots, vendor active, PO / SO belongs to the vendor and is released, receipt posted, **full received value** (±`FullValueTolerance`), currency, duplicates | System | `SubmissionProcessor`, `AutomaticChecks` |
| 5 | Exceptions only (possible duplicate, BC unreachable) | Finance Manager | `ExceptionReview` |
| 6 | Confirmatory check (what must be read on the documents) | Accounts | `Accept` — **official submission; vendor told; credit days start** (`AcceptedAtUtc`) |
| 7 | BC entry, payment, close | Accounts / BC | unchanged |

- The Document Controller step is removed. Email intake is switched off (`Vim:EmailIntakeEnabled`, default `false`); the code remains for the archive.
- A check failure at step 4 rejects automatically with the reasons; a rejection by Accounts or the Finance Manager also carries reasons. No credit days run while a case is returned.
- Targets (DMD, 19 Oct 2026): 3 working days from delivery to acceptance. VIM enforces 1 working day each for Accounts and for the Finance Manager (`AccountsCheckWorkingDays`, `ExceptionReviewWorkingDays`), with a reminder half-way and escalation (Accounts → Finance Manager; Finance Manager → `ManagementEscalationEmail`).
- Segregation of duties: whoever keyed in an assisted submission, or cleared a blocking flag, cannot accept the case.

## Still open

- Per-category exceptions to full-value invoicing (milestones on PMC / GMC service orders).
- Measuring step 1 → 2 (delivery to GRV) needs the vendor to record delivery on the portal; planned with the portal.
- Bank details: Business Central API v2.0 does not expose vendor bank accounts; Accounts compares by eye (checklist `ACC_BANK`) until a custom API page exists.
