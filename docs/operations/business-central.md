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

## How the platform signs in (no secret)

Business Central lives in its **own Entra tenant, `desiconerp.com`**, separate
from the tenant the VIM apps run in. The platform reaches it the same way the
Cost Control platform (DCCP) does, with **no client secret anywhere**:

1. The web app or function app asks Azure for a token as its **managed
   identity** (audience `api://AzureADTokenExchange`).
2. The **VIM Business Central connector** app registration (multi-tenant,
   created by `infra/scripts/bootstrap-bc-connector.ps1`) trusts that
   identity through a **federated credential**, and exchanges the token in
   the `desiconerp.com` tenant for a Business Central API token.
3. Business Central lets the connector in because it is listed on its
   **Microsoft Entra Applications** page with the **D365 READ** permission set.

Entra only offers `API.ReadWrite.All` for Business Central; the D365 READ
permission set inside BC is what keeps the connector read-only. Every request
also carries `Data-Access-Intent: ReadOnly`.

VIM has its **own** connector rather than sharing DCCP's, so each platform's
permissions and audit trail stay separate.

## Configuration

| Setting | Example | Notes |
|---|---|---|
| `BusinessCentral:Enabled` | `true` | Master switch. Default `false`: no calls are made and checks are skipped. |
| `BusinessCentral:TenantId` | `desiconerp.com` | The tenant BC lives in. |
| `BusinessCentral:ClientId` | GUID | The VIM connector's client ID (printed by the bootstrap script). |
| `BusinessCentral:AuthMode` | `Federated` | `Federated` in Azure. `Cli` on a developer PC: uses your own `az login --tenant desiconerp.com --allow-no-subscriptions`. |
| `BusinessCentral:Environment` | `Sandbox` / `Production` | Sandbox for dev and uat; Production for prod only. |
| `BusinessCentral:CompanyName` | `Desicon Engineering Ltd` | Looked up once to find the company ID. |
| `BusinessCentral:CompanyId` | GUID | Optional; skips the lookup. |
| `BusinessCentral:LocalCurrencyCode` | `NGN` (default) | BC leaves the currency empty for local currency; this value is used instead. |

The gateway is active only when `Enabled` is `true` and the tenant, client ID
(unless `Cli`), environment and company are set. Otherwise a "disabled"
gateway is used and the workflow runs without BC checks.

### Setting up access (once per environment)

1. Deploy the environment first (the apps' managed identities must exist).
2. In the **main** tenant: `.\infra\scripts\bootstrap-bc-connector.ps1 -Environment dev`.
   It creates the connector, trusts the web and function app identities and
   prints the next steps.
3. As **Global Administrator of `desiconerp.com`**: open the printed
   admin-consent link and accept.
4. In Business Central (**Sandbox** for dev/uat, **Production** for prod):
   **Microsoft Entra Applications > New**: the connector's Client ID,
   State = Enabled, permission set **D365 READ**, company Desicon Engineering Ltd.
   Never SUPER or a write permission set.
5. Set GitHub variables on `<env>-plan` and `<env>`: `BC_TENANT_ID = desiconerp.com`,
   `BC_ENVIRONMENT = Sandbox` (or `Production`), `BC_CLIENT_ID = <client id>`.
   Then set `vim_settings.business_central_enabled = true` and deploy.

If BC answers 403 for a page added by a Desicon extension, the extension's own
read permission set may also be needed (the same note as in DCCP's setup guide).

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
