# Business Central integration (read-only)

The VIM platform **reads** from Dynamics 365 Business Central (BC) to check
invoices. It never creates, posts or pays anything in BC. BC remains the
financial system of record; AP staff still enter purchase invoices in BC
themselves and record the BC document number on the case.

Code: `src/Desicon.Vim.Infrastructure/Erp/BusinessCentralGateway.cs`.

## What the platform checks

| Check | When | BC API (standard v2.0) |
|---|---|---|
| Is the sender's email on a vendor record? | Email intake | `vendors?$filter=email eq '...'` |
| Does the vendor exist, and is it blocked? | Registration | `vendors?$filter=number eq '...'` |
| Does the PO/SO exist, belong to this vendor, is it open, has anything been received, what is left to invoice, which currency? | Registration | `purchaseOrders?$filter=number eq '...'&$expand=purchaseOrderLines` |
| Has the invoice been paid? | Payment synchronisation (future job) | `purchaseInvoices?$filter=number eq '...'` |

Problems are raised as **flags** on the case for people to review; a BC check
never silently rejects an invoice.

## Configuration

| Setting | Example | Notes |
|---|---|---|
| `BusinessCentral:Enabled` | `true` | Master switch. Default `false`: no calls are made and checks are skipped. |
| `BusinessCentral:TenantId` | GUID | Desicon's Entra tenant id. |
| `BusinessCentral:ClientId` | GUID | App registration used only for BC. |
| `BusinessCentral:ClientSecret` | Key Vault reference | Never in source control or plain app settings. |
| `BusinessCentral:Environment` | `Production` / `Sandbox` | BC environment name. |
| `BusinessCentral:CompanyId` | GUID | From `GET .../api/v2.0/companies`. |
| `BusinessCentral:LocalCurrencyCode` | `NGN` (default) | BC leaves the currency empty for local currency; this value is used instead. |

The gateway is active only when `Enabled` is `true` **and** all the ids and
the secret are present. Otherwise a "disabled" gateway is used and the
workflow runs without BC checks.

### Setting up access in BC (Global Administrator task)

1. In Entra ID, create an app registration (e.g. `vim-bc-reader-prod`), add
   the API permission **Dynamics 365 Business Central > Application
   permissions > API.ReadWrite.All**, and grant admin consent. (BC has no
   read-only application permission; access is restricted inside BC in the
   next step.)
2. In Business Central, open **Microsoft Entra Applications**, add the
   app's Client ID, set State = Enabled, and assign permission sets that give
   **read-only** access to vendors, purchase orders/lines and purchase
   invoices for the Desicon company only (e.g. a custom permission set with
   Read on those tables; do not assign SUPER or D365 BUS FULL ACCESS).
3. Put the client secret in Key Vault; the function and web apps read it via
   a Key Vault reference (see `infra/terraform/modules/app-hosting`).
4. Test in a **Sandbox** environment first.

## Assumptions to validate with Finance and the BC partner

These follow the standard BC API v2.0. Customisations at Desicon may change
them; confirm each one in the Sandbox before enabling in production.

1. **Purchase order status.** The API reports `Draft`, `In Review` or `Open`;
   `Open` corresponds to **Released** in the BC client. The platform treats
   only `Open` as "open for invoicing". A draft PO is flagged.
2. **Fully invoiced orders disappear.** BC deletes a purchase order once it
   is fully received and invoiced, so a lookup for such an order returns
   "not found". The flag text says "order not found"; reviewers should check
   posted documents in BC in that case.
3. **Goods received (GRV).** The standard API has no filter for posted
   receipts by order number, so the platform checks the order's lines:
   any line with `receivedQuantity > 0` (or `fullyReceived = true` on the
   order) counts as "has a posted receipt". This works for both materials
   (PO) and service orders, provided service orders are BC purchase orders.
4. **Remaining amount.** Calculated as the sum over lines of
   `amountIncludingTax × (quantity − invoicedQuantity) / quantity`, i.e. the
   not-yet-invoiced value including tax. Invoices above it are flagged.
5. **Email matching.** The vendor lookup compares the vendor card's single
   `email` field with the sender address. BC filters on text are
   case-sensitive, and vendors with several contact addresses (contacts, not
   the vendor card) will not match: expect some "unknown sender" flags until
   vendor emails are cleaned up. The flag is non-blocking.
6. **Payment status.** Posted purchase invoices appear in `purchaseInvoices`
   with status `Open` or `Paid`. The API does **not** expose the payment date
   or payment document number. The platform therefore records:
   - payment reference = the BC invoice number;
   - paid on = the date BC last modified the invoice (`lastModifiedDateTime`),
     which is normally when the payment closed it, but is an **approximation**.
   AP should record the real AP-number/date (via a custom API, see below) if
   that matters for reporting.
7. **Number recorded by AP.** Payment lookup expects the **posted** purchase
   invoice number. If AP records the unposted (draft) number, the lookup will
   not find the posted invoice.

## Not available from the standard API: vendor bank accounts

The fraud check "bank account on the invoice does not match the vendor
record" needs the vendor's bank accounts. **Standard BC API v2.0 does not
expose vendor bank accounts**, so the gateway currently returns an empty list
and the comparison is skipped (the code is already in place in
`CaseWorkflowService`).

**Phase 2:** a small AL extension publishing a read-only custom API page,
for example:

- `GET .../api/desicon/vim/v1.0/companies({id})/vendorBankAccounts?$filter=vendorNumber eq 'V0001'`
  returning `vendorNumber`, `code`, `bankAccountNumber`, `iban`, `swiftCode`.
- Optionally `vendorLedgerEntries` / `vendorPayments` (posted) to return the
  true payment date and payment document number (replacing assumption 6).

Once the page exists, only `BusinessCentralGateway` needs to change; the
workflow code does not.

## Failure behaviour

- Requests retry automatically on throttling (HTTP 429) and temporary server
  errors (standard .NET resilience handler).
- If BC is still unavailable, the action that needed it fails with an error
  and is retried: email intake retries on the next 5-minute run; a user
  registering a case sees an error and can try again. Failures are logged
  with the URL and BC's error message (no secrets are logged).
