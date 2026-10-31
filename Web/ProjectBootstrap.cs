using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Preparation.Shared.Data;
using Preparation.Shared.Security;

namespace Preparation.Shared.Web;

public sealed record ProjectInfo(string Key, string Title, string Database, int Port, string Purpose);

public static class ProjectBootstrap
{
    public static void Configure(WebApplicationBuilder builder, ProjectInfo info)
    {
        builder.Services.AddSingleton(info);
        builder.Services.AddControllersWithViews().AddApplicationPart(typeof(ProjectController).Assembly);
        DemoAuth.AddServices(builder.Services, info.Key);
        var provider = builder.Configuration["Storage:Provider"] ?? "InMemory";
        if (provider.Equals("SqlServer", StringComparison.OrdinalIgnoreCase))
        {
            var connection = builder.Configuration.GetConnectionString("Default");
            if (string.IsNullOrWhiteSpace(connection)) throw new InvalidOperationException("Configura ConnectionStrings__Default antes de usar SQL Server.");
            builder.Services.AddDbContext<AppDbContext>(options => options.UseSqlServer(connection));
        }
        else builder.Services.AddDbContext<AppDbContext>(options => options.UseInMemoryDatabase(info.Database));
    }

    public static async Task InitializeAsync(WebApplication app, ProjectInfo info)
    {
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await db.Database.EnsureCreatedAsync();
        await DemoSeed.SeedAsync(db, info.Key);
    }

    public static void ConfigurePipeline(WebApplication app, ProjectInfo info)
    {
        app.UseStaticFiles();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapGet("/health/live", () => Results.Ok(new { status = "ok", app = info.Key, at = DateTimeOffset.UtcNow }));
        app.MapGet("/api/dashboard", async (AppDbContext db, ClaimsPrincipal user) => Results.Ok(await DashboardData.GetAsync(db, info.Key, user))).RequireAuthorization();
        DemoAuth.MapEndpoints(app, info.Key);
        ProjectApi.Map(app, info);
        app.MapControllerRoute("default", "{controller=Project}/{action=Index}/{id?}");
    }
}

[Route("/")]
public sealed class ProjectController(ProjectInfo info) : Controller
{
    [HttpGet]
    public IActionResult Index() => Content(DashboardRenderer.Render(info), "text/html; charset=utf-8");
}

public static class DashboardData
{
    public static Task<object> GetAsync(AppDbContext db, string kind, ClaimsPrincipal user) => kind switch
    {
        "helpdesk" => HelpDeskAsync(db, user),
        "inventory" => InventoryAsync(db),
        "wallet" => WalletAsync(db, user),
        "documents" => DocumentsAsync(db, user),
        "inspections" => InspectionsAsync(db, user),
        "legacy" => LegacyAsync(db),
        "hr" => HrAsync(db, user),
        _ => Task.FromResult<object>(new { })
    };

    private static async Task<object> HelpDeskAsync(AppDbContext db, ClaimsPrincipal user)
    {
        var query = db.Tickets.AsQueryable();
        if (user.IsInRole("Solicitante")) query = query.Where(x => x.Requester == user.Identity!.Name);
        var tickets = await query.OrderByDescending(x => x.CreatedAt).Take(100).ToListAsync();
        var ids = tickets.Select(x => x.Id).ToList();
        var events = await db.TicketEvents.Where(x => ids.Contains(x.TicketId)).OrderByDescending(x => x.At).Take(30).ToListAsync();
        return new { tickets, events };
    }

    private static async Task<object> InventoryAsync(AppDbContext db) => new
    {
        products = await db.Products.ToListAsync(),
        balances = await (from b in db.InventoryBalances join p in db.Products on b.ProductId equals p.Id select new { p.Sku, p.Name, p.MinStock, b.WarehouseId, b.Quantity }).ToListAsync(),
        movements = await db.StockMovements.OrderByDescending(x => x.At).Take(50).ToListAsync()
    };

    private static async Task<object> WalletAsync(AppDbContext db, ClaimsPrincipal user)
    {
        var accountQuery = db.DemoAccounts.AsQueryable();
        if (user.IsInRole("Usuario")) accountQuery = accountQuery.Where(x => x.Owner == user.Identity!.Name);
        var accountRows = await accountQuery.ToListAsync();
        var ids = accountRows.Select(x => x.Id).ToList();
        var accounts = new List<object>();
        foreach (var account in accountRows)
        {
            var balance = await db.LedgerEntries.Where(x => x.AccountId == account.Id).SumAsync(x => (decimal?)x.Amount) ?? 0m;
            accounts.Add(new { account.Id, account.Owner, account.Code, account.Currency, account.Active, balance });
        }
        var transferQuery = db.Transfers.AsQueryable();
        if (user.IsInRole("Usuario")) transferQuery = transferQuery.Where(x => ids.Contains(x.SourceAccountId) || ids.Contains(x.DestinationAccountId));
        var transfers = await transferQuery.OrderByDescending(x => x.CreatedAt).Take(50).ToListAsync();
        return new { accounts, transfers };
    }

    private static async Task<object> DocumentsAsync(AppDbContext db, ClaimsPrincipal user)
    {
        var query = db.Documents.Where(x => x.RetiredAt == null).AsQueryable();
        if (user.IsInRole("Autor")) query = query.Where(x => x.Owner == user.Identity!.Name || x.Confidentiality != "Restringido");
        var documents = await query.OrderByDescending(x => x.CreatedAt).Take(100).ToListAsync();
        var ids = documents.Select(x => x.Id).ToList();
        var approvals = await db.ApprovalRequests.Where(x => ids.Contains(x.DocumentId)).OrderByDescending(x => x.CreatedAt).Take(100).ToListAsync();
        return new { documents, approvals };
    }

    private static async Task<object> InspectionsAsync(AppDbContext db, ClaimsPrincipal user)
    {
        var query = db.Inspections.AsQueryable();
        if (user.IsInRole("Inspector")) query = query.Where(x => x.Inspector == user.Identity!.Name);
        var inspections = await query.OrderByDescending(x => x.StartedAt).ToListAsync();
        var ids = inspections.Select(x => x.Id).ToList();
        var findings = await db.Findings.Where(x => ids.Contains(x.InspectionId)).ToListAsync();
        return new
        {
            sites = await db.Sites.ToListAsync(),
            templates = await db.InspectionTemplates.ToListAsync(),
            inspections,
            findings
        };
    }

    private static async Task<object> LegacyAsync(AppDbContext db) => new
    {
        requests = await db.LegacyRequests.ToListAsync(),
        changes = await db.MigrationChanges.OrderByDescending(x => x.At).ToListAsync(),
        migration = new[] { new { module = "Solicitudes", state = "Baseline disponible" }, new { module = "Persistencia EF Core", state = "Pendiente" }, new { module = "API moderna", state = "En preparación" }, new { module = "Despliegue y reversión", state = "Pendiente" } }
    };

    private static async Task<object> HrAsync(AppDbContext db, ClaimsPrincipal user)
    {
        var employeeQuery = db.Employees.AsQueryable();
        if (user.IsInRole("Empleado")) employeeQuery = employeeQuery.Where(x => x.Code == (user.Identity!.Name == "employee" ? "EMP-001" : ""));
        if (user.IsInRole("Supervisor")) employeeQuery = employeeQuery.Where(x => x.Supervisor == user.Identity!.Name);
        var employees = await employeeQuery.ToListAsync();
        var ids = employees.Select(x => x.Id).ToList();
        var attendance = await db.AttendanceEntries.Where(x => ids.Contains(x.EmployeeId)).OrderByDescending(x => x.Date).ThenByDescending(x => x.Time).Take(100).ToListAsync();
        var leaveRequests = await db.LeaveRequests.Where(x => ids.Contains(x.EmployeeId)).OrderByDescending(x => x.CreatedAt).Take(100).ToListAsync();
        return new { employees, attendance, leaveRequests };
    }
}
