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

## `product.changed` (version 1)

A product was added, edited or deactivated at a register (Manage → PLU), or the owner pressed **Send all products** on the Head Office screen. The payload is the **whole product as it is now**, so the newest event is always enough. Cost price is never sent.

| Field | Type | Meaning |
| --- | --- | --- |
| `productId` | GUID | The product's id in every system (`Product.SyncId` on the register; the register's own number stays local) |
| `name`, `category` | text | Name and department |
| `price` | number | Shelf price, AUD inc GST |
| `barcode`, `plu` | text | Main barcode and PLU, `null` when blank |
| `enableOnWebsite` | true/false | The register's "Enable on website" tick, a hint only: the website decides what it shows |
| `isActive` | true/false | `false` = deactivated at the register; stop selling it online too |
| `variants` | list | Other pack sizes: `packType` (`Single`, `Pack`, `SplitCase`, `SplitCase2`, `FullCase`), `barcode`, `plu`, `unitQuantity`, `price`, `isActive` |

```json
{
  "eventId": "5d2e8f1a-3b4c-4d5e-9f60-718293a4b5c6",
  "type": "product.changed",
  "version": 1,
  "storeId": "STORE-001",
  "registerId": "REG-001",
  "sequence": 1844,
  "occurredAtUtc": "2026-11-14T13:05:00Z",
  "payload": {
    "productId": "8f14e45f-ceea-4672-a4c8-1f0b6c2d3e4a",
    "name": "Heineken 6-Pack",
    "category": "Beer",
    "price": 23.50,
    "barcode": "9300000000011",
    "plu": "101",
    "enableOnWebsite": true,
    "isActive": true,
    "variants": [
      { "packType": "FullCase", "barcode": "9300000000099", "plu": null, "unitQuantity": 24, "price": 65.00, "isActive": true }
    ]
  }
}
```

Head Office keeps the latest copy per store (`CatalogItems`). A change that is older than the one it holds (it arrived late) is stored as an event but doesn't overwrite the newer product. A product with no `productId` or name, a negative price, or a field that is too long is rejected.

The website reads the catalog with `GET /api/v1/catalog/changes?storeId=…&after=N` (website key):

- returns products changed after change number `N`, oldest first, up to 500 (`limit`, max 1000);
- keep `next` and send it as `after` next time; while `more` is true, ask again straight away;
- `after=0` returns the whole catalog. Asking again from an older number is harmless: apply products by `productId`.

Types: `NepPOS.Contracts.Catalog` (`CatalogChanges`, `CatalogProduct`).

## How Head Office answers

`POST /api/v1/sync/events` takes up to 500 events (`{"events":[…]}`) and answers, per event:

- **accepted**: stored now;
- **duplicates**: already stored (a resend). The register marks both of these as sent;
- **rejected** (with a reason): for another store or register, a reused sequence number, or a bad envelope. It stays unsent at the register for a person to check.

`lastSequence` is the highest sequence Head Office holds for that register, so the register can see if anything is missing.

## Coming in later build steps

`stock.changed`, `points.changed`, online order status, and the webhooks Head Office sends to the website are added here as they are built.
