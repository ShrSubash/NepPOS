# Stock, packs and importing from the old POS

## Stock ledger

Every change to a product's stock is one `StockMovement` row, in **singles**: positive in, negative out. `Product.StockOnHand` is where the movements add up to, and both are saved in the same transaction as whatever caused the change.

| Type | Written by |
|---|---|
| `Opening` | Old POS import, CSV import of a new product, a new product typed with stock |
| `Sale` | Every completed sale: −quantity × pack units (a case of 24 is −24); refund lines put stock back |
| `Adjustment` | Stock changed by hand on Products & PLUs, or by a CSV import |
| `Receipt`, `Stocktake`, `Wastage`, `TransferOut`, `TransferIn` | Reserved for receiving, stocktakes, ullages and transfers (next steps) |

Rows are never edited or deleted. To see why stock is what it is: `SELECT * FROM StockMovements WHERE ProductId = … ORDER BY AtUtc`.

## Packs and barcodes

- A product sells as a **single** at `Product.Price`, plus any number of **packs** (`ProductVariant`): 6-pack, case of 24, "2 for", each with its own price and `UnitQuantity` (singles per pack).
- The product and each pack have one main barcode (`Barcode`) and any number of extra ones (`ProductBarcode`). A barcode can only belong to one product or pack (unique `LookupKey`).
- Scanning (digits only; vouchers `GC-` and members `CUST-` go elsewhere): PLU first, then main barcodes, then extra barcodes. Barcodes match with or without leading zeros, so a UPC printed as `82184045954` is found when the scanner sends `082184045954`. EAN-8, UPC-A, EAN-13 and GTIN-14 are all accepted.
- **GST-free** products (`Product.IsGstFree`) are rung up with 0% GST; everything else with 10% included.

## Importing from the old POS (myPOS / RSA PCPOS)

Manage → Products & PLUs → **From old POS**. In SQL Server Management Studio on the old database, run each query, select the whole grid, right-click → **Copy with Headers**, paste into Notepad and save as `.txt`:

| File | Query | |
|---|---|---|
| Products | `SELECT * FROM dbo.plu` | required |
| Department names | `SELECT Number, dpn FROM dbo.dept` | optional (else "Department 12") |
| Supplier names | `SELECT Number, supplier FROM dbo.supplier` | optional (else "Supplier 3") |

The screen shows what will be made (products, packs, barcodes, stock), what is skipped and why, and notes, before anything is saved. Products whose PLU is already in NepPOS are left alone, so the import can be run again safely.

| Old POS (`dbo.plu`) | NepPOS |
|---|---|
| `Number` | PLU |
| `longdescrip` (else `description`) | Name |
| `singleprice`, `P1` barcodes | The product |
| `splitprice` × `splitsize`, `P2` | Pack of *n* |
| `price4`, `P7` / `P4` | 6-pack |
| `packprice` × `packsize`, `P3` | Case of *n* |
| `split2price` × `split2size`, `P9` / `P6` | Pack of *n* |
| `averagecost` (else `singlecost`) | Cost per single |
| `SOH` | Opening stock movement |
| `dept` | Department (name from the list) |
| `supplier`, `suppcodes1–10` | Supplier and its item codes |
| `taxcode` = A | GST-free |
| `Status` = 9 | Inactive |
| `onlineshopenabled` = 1 | Shown on the website |

A pack is only made when it has a price; its barcodes are noted and left out otherwise. Not carried over: running totals (`psm`, `psy`, `month1–12`, `purchasesm*`, `q*`, `points*`), old prices, wholesale prices, size stock, printers and triggers. Reports work sales out from the sales themselves.

## Database change

New tables `ProductBarcodes`, `StockMovements`, `Suppliers`, `ProductSupplierCodes`; new columns `Products.IsGstFree`, `Products.SupplierId`, `ProductVariants.Name`, `SaleLineItems.ProductId`, `SaleLineItems.PackUnits`. Migration `AddStockLedger` (the register applies it on start).
