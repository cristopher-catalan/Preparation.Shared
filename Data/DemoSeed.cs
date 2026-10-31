using Microsoft.EntityFrameworkCore;
using Preparation.Shared.Domain;

namespace Preparation.Shared.Data;

public static class DemoSeed
{
    public static async Task SeedAsync(AppDbContext db, string kind)
    {
        switch (kind)
        {
            case "helpdesk":
                if (!await db.Tickets.AnyAsync()) db.Tickets.Add(new Ticket { Number = 1001, Title = "No puedo conectarme al Wi-Fi", Description = "El equipo pierde conexión durante las reuniones.", Category = "Redes", Priority = "Alta", Status = "Nuevo", Requester = "requester" });
                break;
            case "inventory":
                if (!await db.Warehouses.AnyAsync()) db.Warehouses.Add(new Warehouse { Id = 1, Code = "CENTRAL", Name = "Almacén central", Location = "Lima" });
                if (!await db.Products.AnyAsync()) db.Products.AddRange(new Product { Id = 1, Sku = "MOU-001", Name = "Mouse USB", Unit = "unidad", Cost = 18, Price = 29.90m, MinStock = 3 }, new Product { Id = 2, Sku = "TEC-001", Name = "Teclado USB", Unit = "unidad", Cost = 35, Price = 54.90m, MinStock = 2 });
                await db.SaveChangesAsync();
                if (!await db.InventoryBalances.AnyAsync()) db.InventoryBalances.AddRange(new InventoryBalance { ProductId = 1, WarehouseId = 1, Quantity = 12 }, new InventoryBalance { ProductId = 2, WarehouseId = 1, Quantity = 8 });
                break;
            case "wallet":
                if (!await db.DemoAccounts.AnyAsync())
                {
                    db.DemoAccounts.AddRange(new DemoAccount { Id = 1, Owner = "customer", Code = "DEMO-1001" }, new DemoAccount { Id = 2, Owner = "receiver", Code = "DEMO-1002" });
                    await db.SaveChangesAsync();
                    db.LedgerEntries.Add(new LedgerEntry { AccountId = 1, Amount = 250, Kind = "Saldo inicial ficticio" });
                }
                break;
            case "documents":
                if (!await db.Documents.AnyAsync()) db.Documents.Add(new DocumentRecord { Id = 1, Owner = "author", Title = "Procedimiento de ejemplo", Description = "Documento de demostración sin archivo adjunto.", Category = "Operaciones", Confidentiality = "Interno", State = "Borrador" });
                break;
            case "inspections":
                if (!await db.Sites.AnyAsync()) db.Sites.Add(new Site { Id = 1, Code = "LIM-01", Name = "Sede de demostración", Address = "Lima" });
                if (!await db.InspectionTemplates.AnyAsync()) db.InspectionTemplates.Add(new InspectionTemplate { Id = 1, Title = "Seguridad básica", Version = 1 });
                await db.SaveChangesAsync();
                if (!await db.InspectionQuestions.AnyAsync()) db.InspectionQuestions.AddRange(new InspectionQuestion { Id = 1, TemplateId = 1, Prompt = "¿Las rutas de evacuación están despejadas?", Points = 5 }, new InspectionQuestion { Id = 2, TemplateId = 1, Prompt = "¿Los extintores tienen inspección vigente?", Points = 5 });
                break;
            case "legacy":
                if (!await db.LegacyRequests.AnyAsync()) db.LegacyRequests.Add(new LegacyRequest { Number = "LEG-0001", Title = "Actualizar dirección de contacto", Description = "Caso sintético para comparar comportamiento antes y después.", State = "Abierta", Priority = "Normal" });
                if (!await db.MigrationChanges.AnyAsync()) db.MigrationChanges.Add(new MigrationChange { Module = "Solicitudes", Change = "Baseline de comportamiento documentado", Risk = "Bajo", Rollback = "Restaurar el snapshot anterior" });
                break;
            case "hr":
                if (!await db.Departments.AnyAsync()) db.Departments.Add(new Department { Id = 1, Name = "Operaciones" });
                if (!await db.Employees.AnyAsync()) db.Employees.AddRange(new Employee { Id = 1, Code = "EMP-001", Name = "Ana Demo", Email = "employee@demo.local", Department = "Operaciones", Supervisor = "supervisor", HireDate = new DateOnly(2025, 1, 15) }, new Employee { Id = 2, Code = "EMP-002", Name = "Luis Demo", Email = "luis@demo.local", Department = "Operaciones", Supervisor = "supervisor", HireDate = new DateOnly(2024, 6, 1) });
                break;
        }
        await db.SaveChangesAsync();
    }
}
