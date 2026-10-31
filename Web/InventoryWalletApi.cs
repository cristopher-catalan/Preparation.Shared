using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Preparation.Shared.Data;
using Preparation.Shared.Domain;

namespace Preparation.Shared.Web;

internal static class InventoryApi
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    public static void Map(WebApplication app)
    {
        var api = app.MapGroup("/api").RequireAuthorization();
        api.MapGet("/products", async (AppDbContext db) => Results.Ok(await db.Products.OrderBy(x => x.Name).ToListAsync()));
        api.MapPost("/products", async (ProductCreate input, AppDbContext db) =>
        {
            if (string.IsNullOrWhiteSpace(input.Sku) || string.IsNullOrWhiteSpace(input.Name) || input.Price < 0 || input.Cost < 0 || input.MinStock < 0) return Results.BadRequest(new { error = "SKU, nombre y valores no negativos son obligatorios." });
            if (await db.Products.AnyAsync(x => x.Sku == input.Sku.Trim())) return Results.Conflict(new { error = "El SKU ya existe." });
            var product = new Product { Sku = input.Sku.Trim().ToUpperInvariant(), Name = input.Name.Trim(), Unit = string.IsNullOrWhiteSpace(input.Unit) ? "unidad" : input.Unit.Trim(), Cost = input.Cost, Price = input.Price, MinStock = input.MinStock };
            db.Products.Add(product); await db.SaveChangesAsync(); return Results.Created($"/api/products/{product.Id}", product);
        }).Roles("Administrador");
        api.MapGet("/inventory", async (AppDbContext db, int? warehouseId) =>
        {
            var query = from b in db.InventoryBalances join p in db.Products on b.ProductId equals p.Id select new { p.Id, p.Sku, p.Name, p.MinStock, b.WarehouseId, b.Quantity, bajo = b.Quantity <= p.MinStock };
            if (warehouseId.HasValue) query = query.Where(x => x.WarehouseId == warehouseId.Value);
            return Results.Ok(await query.OrderBy(x => x.Name).ToListAsync());
        });
        api.MapGet("/stock-movements", async (AppDbContext db, int? productId, int page = 1, int pageSize = 50) =>
        {
            page = Math.Clamp(page, 1, 500); pageSize = Math.Clamp(pageSize, 1, 100);
            var query = db.StockMovements.AsQueryable(); if (productId.HasValue) query = query.Where(x => x.ProductId == productId);
            return Results.Ok(new { total = await query.CountAsync(), items = await query.OrderByDescending(x => x.At).Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(), page, pageSize });
        });
        api.MapPost("/purchases", async (PurchaseCreate input, AppDbContext db, System.Security.Claims.ClaimsPrincipal user) =>
        {
            if (string.IsNullOrWhiteSpace(input.Supplier) || input.Quantity <= 0 || input.UnitCost < 0 || !await db.Products.AnyAsync(x => x.Id == input.ProductId && x.Active)) return Results.BadRequest(new { error = "Proveedor, producto activo y cantidades válidas son obligatorios." });
            var purchase = new Purchase { Supplier = input.Supplier.Trim() }; db.Purchases.Add(purchase); await db.SaveChangesAsync(); db.PurchaseLines.Add(new PurchaseLine { PurchaseId = purchase.Id, ProductId = input.ProductId, Quantity = input.Quantity, UnitCost = input.UnitCost });
            await db.SaveChangesAsync(); await ApiHelpers.SaveAuditAsync(db, ApiHelpers.User(user), "Compra creada", "Purchase", purchase.Id, input.Supplier);
            return Results.Created($"/api/purchases/{purchase.Id}", purchase);
        }).Roles("Almacenero", "Administrador");
        api.MapPost("/purchases/{id:int}/receive", async (int id, ReceiveCreate input, AppDbContext db, System.Security.Claims.ClaimsPrincipal user) =>
        {
            if (input.Quantity <= 0) return Results.BadRequest(new { error = "La cantidad recibida debe ser mayor que cero." });
            await Gate.WaitAsync();
            try
            {
                var purchase = await db.Purchases.FindAsync(id); if (purchase is null) return Results.NotFound();
                var line = await db.PurchaseLines.FirstOrDefaultAsync(x => x.PurchaseId == id); if (line is null) return Results.Conflict(new { error = "La compra no tiene líneas." });
                var remaining = line.Quantity - line.Received; if (input.Quantity > remaining) return Results.BadRequest(new { error = "La recepción supera la cantidad pendiente.", remaining });
                var balance = await db.InventoryBalances.FirstOrDefaultAsync(x => x.ProductId == line.ProductId && x.WarehouseId == 1);
                if (balance is null) { balance = new InventoryBalance { ProductId = line.ProductId, WarehouseId = 1 }; db.InventoryBalances.Add(balance); }
                balance.Quantity += input.Quantity; balance.Version++;
                line.Received += input.Quantity; purchase.State = line.Received == line.Quantity ? "Recibida" : "Parcial";
                db.StockMovements.Add(new StockMovement { ProductId = line.ProductId, WarehouseId = 1, Type = "Compra", Delta = input.Quantity, Reference = $"Compra {id}", Actor = ApiHelpers.User(user) });
                await db.SaveChangesAsync(); await ApiHelpers.SaveAuditAsync(db, ApiHelpers.User(user), "Recepción de compra", "Purchase", id, input.Quantity.ToString());
                return Results.Ok(new { purchase, line, balance });
            }
            finally { Gate.Release(); }
        }).Roles("Almacenero", "Administrador");
        api.MapPost("/sales", async (SaleCreate input, AppDbContext db, System.Security.Claims.ClaimsPrincipal user) =>
        {
            if (input.Lines is null || input.Lines.Count == 0 || input.Lines.Any(x => x.Quantity <= 0)) return Results.BadRequest(new { error = "Agrega al menos una línea con cantidades positivas." });
            await Gate.WaitAsync();
            try
            {
                var grouped = input.Lines.GroupBy(x => x.ProductId).Select(g => new SaleLineInput(g.Key, g.Sum(x => x.Quantity))).ToList();
                var products = await db.Products.Where(x => grouped.Select(y => y.ProductId).Contains(x.Id) && x.Active).ToDictionaryAsync(x => x.Id);
                if (products.Count != grouped.Count) return Results.BadRequest(new { error = "Producto inexistente o inactivo." });
                var balances = new Dictionary<int, InventoryBalance>();
                foreach (var line in grouped)
                {
                    var balance = await db.InventoryBalances.FirstOrDefaultAsync(x => x.ProductId == line.ProductId && x.WarehouseId == input.WarehouseId);
                    if (balance is null || balance.Quantity < line.Quantity) return Results.Conflict(new { error = "Existencias insuficientes; no se registró la venta.", productId = line.ProductId, available = balance?.Quantity ?? 0 });
                    balances[line.ProductId] = balance;
                }
                Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction? transaction = null;
                if (db.Database.IsRelational()) transaction = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable);
                try
                {
                    var subtotal = grouped.Sum(x => products[x.ProductId].Price * x.Quantity); var sale = new Sale { WarehouseId = input.WarehouseId, Subtotal = subtotal, Tax = decimal.Round(subtotal * 0.18m, 2), Total = decimal.Round(subtotal * 1.18m, 2) };
                    db.Sales.Add(sale);
                    foreach (var line in grouped)
                    {
                        var product = products[line.ProductId]; var balance = balances[line.ProductId]; balance.Quantity -= line.Quantity; balance.Version++;
                        db.SaleLines.Add(new SaleLine { Sale = sale, ProductId = product.Id, Quantity = line.Quantity, UnitPrice = product.Price });
                        db.StockMovements.Add(new StockMovement { ProductId = product.Id, WarehouseId = input.WarehouseId, Type = "Venta", Delta = -line.Quantity, Reference = "Venta demo", Actor = ApiHelpers.User(user) });
                    }
                    await db.SaveChangesAsync(); if (transaction is not null) await transaction.CommitAsync();
                    await ApiHelpers.SaveAuditAsync(db, ApiHelpers.User(user), "Venta registrada", "Sale", sale.Id, sale.Total.ToString("0.00"));
                    return Results.Created($"/api/sales/{sale.Id}", sale);
                }
                finally { if (transaction is not null) await transaction.DisposeAsync(); }
            }
            catch (DbUpdateConcurrencyException) { return Results.Conflict(new { error = "El stock cambió mientras se procesaba la venta; vuelve a consultar." }); }
            finally { Gate.Release(); }
        }).Roles("Vendedor", "Administrador");
        api.MapGet("/sales", async (AppDbContext db) => Results.Ok(await db.Sales.OrderByDescending(x => x.CreatedAt).Take(100).ToListAsync()));
        api.MapPost("/sales/{id:int}/cancel", async (int id, AppDbContext db, System.Security.Claims.ClaimsPrincipal user) =>
        {
            await Gate.WaitAsync();
            try
            {
                var sale = await db.Sales.FindAsync(id); if (sale is null) return Results.NotFound(); if (sale.State == "Anulada") return Results.Conflict(new { error = "La venta ya fue anulada." });
                var lines = await db.SaleLines.Where(x => x.SaleId == id).ToListAsync();
                foreach (var line in lines)
                {
                    var balance = await db.InventoryBalances.FirstOrDefaultAsync(x => x.ProductId == line.ProductId && x.WarehouseId == sale.WarehouseId);
                    if (balance is null) return Results.Conflict(new { error = "No existe el balance para reversar." });
                    balance.Quantity += line.Quantity; balance.Version++;
                    db.StockMovements.Add(new StockMovement { ProductId = line.ProductId, WarehouseId = sale.WarehouseId, Type = "Reversa", Delta = line.Quantity, Reference = $"Anulación venta {id}", Actor = ApiHelpers.User(user) });
                }
                sale.State = "Anulada"; sale.CanceledAt = DateTimeOffset.UtcNow; await db.SaveChangesAsync();
                await ApiHelpers.SaveAuditAsync(db, ApiHelpers.User(user), "Venta anulada", "Sale", id); return Results.Ok(sale);
            }
            finally { Gate.Release(); }
        }).Roles("Supervisor", "Administrador");
        api.MapGet("/reports/low-stock", async (AppDbContext db) => Results.Ok(await (from b in db.InventoryBalances join p in db.Products on b.ProductId equals p.Id where b.Quantity <= p.MinStock select new { p.Sku, p.Name, b.WarehouseId, b.Quantity, p.MinStock }).ToListAsync()));
    }
}

internal static class WalletApi
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    public static void Map(WebApplication app)
    {
        var api = app.MapGroup("/api").RequireAuthorization();
        api.MapGet("/accounts", async (AppDbContext db, System.Security.Claims.ClaimsPrincipal user) =>
        {
            var accounts = db.DemoAccounts.AsQueryable(); if (user.IsInRole("Usuario")) accounts = accounts.Where(x => x.Owner == ApiHelpers.User(user));
            var rows = await accounts.ToListAsync(); var result = new List<object>();
            foreach (var account in rows) result.Add(new { account.Id, account.Owner, account.Code, account.Currency, account.Active, balance = await db.LedgerEntries.Where(x => x.AccountId == account.Id).SumAsync(x => x.Amount) });
            return Results.Ok(result);
        });
        api.MapGet("/accounts/{id:int}/ledger", async (int id, AppDbContext db, System.Security.Claims.ClaimsPrincipal user) =>
        {
            var account = await db.DemoAccounts.FindAsync(id); if (account is null) return Results.NotFound(); if (user.IsInRole("Usuario") && account.Owner != ApiHelpers.User(user)) return Results.Forbid();
            return Results.Ok(new { account, entries = await db.LedgerEntries.Where(x => x.AccountId == id).OrderByDescending(x => x.At).ToListAsync() });
        });
        api.MapGet("/transfers/{id:int}", async (int id, AppDbContext db, System.Security.Claims.ClaimsPrincipal user) =>
        {
            var transfer = await db.Transfers.FindAsync(id); if (transfer is null) return Results.NotFound();
            var source = await db.DemoAccounts.FindAsync(transfer.SourceAccountId); var target = await db.DemoAccounts.FindAsync(transfer.DestinationAccountId);
            if (user.IsInRole("Usuario") && source?.Owner != ApiHelpers.User(user) && target?.Owner != ApiHelpers.User(user)) return Results.Forbid();
            return Results.Ok(transfer);
        });
        api.MapPost("/transfers", async (TransferCreate input, AppDbContext db, System.Security.Claims.ClaimsPrincipal user) =>
        {
            if (input.Amount <= 0 || string.IsNullOrWhiteSpace(input.IdempotencyKey) || input.IdempotencyKey.Length > 100) return Results.BadRequest(new { error = "Usa un importe mayor que cero y una clave de idempotencia de hasta 100 caracteres." });
            await Gate.WaitAsync();
            try
            {
                var from = await db.DemoAccounts.FindAsync(input.SourceAccountId); var to = await db.DemoAccounts.FindAsync(input.DestinationAccountId);
                if (from is null || to is null || !from.Active || !to.Active) return Results.BadRequest(new { error = "Las dos cuentas demo deben existir y estar activas." });
                if (from.Id == to.Id) return Results.BadRequest(new { error = "El origen y el destino deben ser distintos." });
                if (user.IsInRole("Usuario") && from.Owner != ApiHelpers.User(user)) return Results.Forbid();
                var prior = await db.Transfers.FirstOrDefaultAsync(x => x.IdempotencyKey == input.IdempotencyKey);
                if (prior is not null) return prior.SourceAccountId == from.Id ? Results.Ok(new { prior, reused = true }) : Results.Conflict(new { error = "La clave idempotente ya está asociada a otra operación." });
                var balance = await db.LedgerEntries.Where(x => x.AccountId == from.Id).SumAsync(x => x.Amount);
                if (balance < input.Amount) return Results.Conflict(new { error = "Saldo ficticio insuficiente.", balance });
                Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction? transaction = null;
                if (db.Database.IsRelational()) transaction = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable);
                try
                {
                    var transfer = new Transfer { SourceAccountId = from.Id, DestinationAccountId = to.Id, Amount = input.Amount, IdempotencyKey = input.IdempotencyKey };
                    db.Transfers.Add(transfer);
                    db.LedgerEntries.AddRange(new LedgerEntry { AccountId = from.Id, Transfer = transfer, Amount = -input.Amount, Kind = "Transferencia demo" }, new LedgerEntry { AccountId = to.Id, Transfer = transfer, Amount = input.Amount, Kind = "Transferencia demo" });
                    await db.SaveChangesAsync(); if (transaction is not null) await transaction.CommitAsync();
                    await ApiHelpers.SaveAuditAsync(db, ApiHelpers.User(user), "Transferencia demo completada", "Transfer", transfer.Id, input.Amount.ToString("0.00"));
                    return Results.Created($"/api/transfers/{transfer.Id}", new { transfer, debit = -input.Amount, credit = input.Amount });
                }
                finally { if (transaction is not null) await transaction.DisposeAsync(); }
            }
            catch (DbUpdateException) { return Results.Conflict(new { error = "La clave idempotente ya fue procesada; consulta la transferencia." }); }
            finally { Gate.Release(); }
        }).Roles("Usuario", "Administrador");
        api.MapPost("/beneficiaries", async (Beneficiary input, AppDbContext db, System.Security.Claims.ClaimsPrincipal user) =>
        {
            if (string.IsNullOrWhiteSpace(input.Alias) || input.Alias.Length > 80) return Results.BadRequest(new { error = "Alias obligatorio (máximo 80 caracteres)." });
            var account = await db.DemoAccounts.FindAsync(input.DestinationAccountId); if (account is null) return Results.NotFound();
            if (user.IsInRole("Usuario")) input.Owner = ApiHelpers.User(user); else if (string.IsNullOrWhiteSpace(input.Owner)) return Results.BadRequest(new { error = "Indica el propietario demo." });
            input.Id = 0; db.Beneficiaries.Add(input); await db.SaveChangesAsync(); return Results.Created($"/api/beneficiaries/{input.Id}", input);
        }).Roles("Usuario", "Administrador");
        api.MapGet("/beneficiaries", async (AppDbContext db, System.Security.Claims.ClaimsPrincipal user) => Results.Ok(await db.Beneficiaries.Where(x => user.IsInRole("Administrador") || x.Owner == ApiHelpers.User(user)).ToListAsync()));
    }
}






