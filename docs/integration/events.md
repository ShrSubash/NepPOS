# Sync events

Every event a NepPOS register sends to Head Office, with its fields and an example.
Change this file in the same pull request as the event (see [README.md](README.md#how-to-keep-it-documented)).
The C# types are in `src/NepPOS.Contracts/Sync/SyncEvents.cs`.

## Rules

- An event type's meaning never changes. A different payload shape gets a new `version`; old versions keep working.
- Fields are only ever **added**. Head Office must ignore fields it doesn't know.
- JSON is camelCase; money is AUD as a number with up to 2 decimals (prices up to 4); enums are text.
- Card details are the masked number and auth code only. A full card number is never sent.

## Envelope

Every event, whatever its type:

| Field | Type | Meaning |
| --- | --- | --- |
| `eventId` | GUID | Unique per event. Head Office ignores a repeat, so resending is always safe |
| `type` | text | One of the types below |
| `version` | number | Payload version for that type |
| `storeId`, `registerId` | text | Where it happened |
| `sequence` | number | 1, 2, 3… per register with no gaps. A gap means an event is missing |
| `occurredAtUtc` | date-time (UTC) | When it happened at the register |
| `payload` | object | The event's details (below) |

The register saves each event in the same database transaction as the sale or journal line it describes (table `OutboxMessages`), so an event exists exactly when its record does.

## `sale.completed` (version 1)

A sale was completed and has a tax invoice number.

| Field | Type | Meaning |
| --- | --- | --- |
| `saleId` | GUID | The sale's id at the register |
| `invoiceNumber` | number | Gapless per store |
| `createdAtUtc` | date-time | When the sale was started |
| `employeeId`, `employeeName` | number, text | Who made the sale |
| `total` | number | Receipt total, GST inclusive, before cash rounding |
| `gst` | number | GST included in `total` |
| `cashRounding` | number | 5c rounding on cash, e.g. `-0.02` |
| `lines[]` | list | `sku`, `name`, `category`, `quantity`, `unitPriceIncGst`, `gstRate` (0.10 = 10%), `lineTotalIncGst` |
| `payments[]` | list | `tender` (`Cash`, `Eftpos`), `amount` (kept), `tendered`, `change`, `authCode`, `cardType`, `maskedPan` |

```json
{
  "eventId": "5f0c2a8e-6a3e-4c1b-9a51-2d1c7e0b9f41",
  "type": "sale.completed",
  "version": 1,
  "storeId": "STORE-001",
  "registerId": "REG-001",
  "sequence": 1842,
  "occurredAtUtc": "2026-11-14T09:12:44Z",
  "payload": {
    "saleId": "8d1e5b7a-2f43-4c8e-9b0a-6a7c1d2e3f40",
    "invoiceNumber": 1001,
    "createdAtUtc": "2026-11-14T09:11:02+00:00",
    "employeeId": 1,
    "employeeName": "Owner",
    "total": 44.00,
    "gst": 4.00,
    "cashRounding": 0,
    "lines": [
      { "sku": "101", "name": "Heineken 6-Pack", "category": "Beer", "quantity": 2, "unitPriceIncGst": 22.00, "gstRate": 0.10, "lineTotalIncGst": 44.00 }
    ],
    "payments": [
      { "tender": "Eftpos", "amount": 44.00, "tendered": 44.00, "change": 0, "authCode": "851898", "cardType": "Mastercard", "maskedPan": "....1025" }
    ]
  }
}
```

## `journal.recorded` (version 1)

A line was written to the register's journal: sign-in and out, voids, removed lines, no sale, card problems, reprints, settings changes, X readings and Z reports.

| Field | Type | Meaning |
| --- | --- | --- |
| `journalId` | GUID | The journal line's id |
| `journalSequence` | number | The journal's own gapless number at that register |
| `eventType` | text | `SignIn`, `SignOut`, `SaleCompleted`, `SaleVoided`, `LineRemoved`, `CardDeclined`, `CardOutcomeUnknown`, `SaleSaveFailed`, `NoSale`, `DrawerOpened`, `ReceiptReprinted`, `SettingsChanged`, `XReport`, `ZReport` |
| `detail` | text | What the Journal screen shows |
| `employeeId`, `employeeName` | number, text | Who was signed in |
| `saleId`, `invoiceNumber` | GUID, number | The sale it is about, if any. For `ZReport`, `invoiceNumber` is the last invoice the Z covers |
| `amount` | number | Money involved, if any (for `ZReport`, the day's gross sales) |

```json
{
  "eventId": "0b6f3c2d-7e1a-4f59-8c3b-2a9d4e5f6a71",
  "type": "journal.recorded",
  "version": 1,
  "storeId": "STORE-001",
  "registerId": "REG-001",
  "sequence": 1843,
  "occurredAtUtc": "2026-11-14T13:02:10Z",
  "payload": {
    "journalId": "c3a1e2f4-5b6d-4e7f-8a9b-0c1d2e3f4a5b",
    "journalSequence": 977,
    "eventType": "ZReport",
    "detail": "Z #12: 142 sales, $6,843.55 gross, $1,812.30 cash",
    "employeeId": 1,
    "employeeName": "Owner",
    "saleId": null,
    "invoiceNumber": 1142,
    "amount": 6843.55
  }
}
```

## How Head Office answers

`POST /api/v1/sync/events` takes up to 500 events (`{"events":[…]}`) and answers, per event:

- **accepted**: stored now;
- **duplicates**: already stored (a resend). The register marks both of these as sent;
- **rejected** (with a reason): for another store or register, a reused sequence number, or a bad envelope. It stays unsent at the register for a person to check.

`lastSequence` is the highest sequence Head Office holds for that register, so the register can see if anything is missing.

## Coming in later build steps

`stock.changed`, `points.changed`, online order status, and the webhooks Head Office sends to the website are added here as they are built.
