using Microsoft.EntityFrameworkCore;
using Preparation.Shared.Domain;

namespace Preparation.Shared.Data;

public sealed class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<Ticket> Tickets => Set<Ticket>(); public DbSet<TicketComment> TicketComments => Set<TicketComment>(); public DbSet<TicketEvent> TicketEvents => Set<TicketEvent>();
    public DbSet<Product> Products => Set<Product>(); public DbSet<Warehouse> Warehouses => Set<Warehouse>(); public DbSet<InventoryBalance> InventoryBalances => Set<InventoryBalance>(); public DbSet<StockMovement> StockMovements => Set<StockMovement>(); public DbSet<Sale> Sales => Set<Sale>(); public DbSet<SaleLine> SaleLines => Set<SaleLine>(); public DbSet<Purchase> Purchases => Set<Purchase>(); public DbSet<PurchaseLine> PurchaseLines => Set<PurchaseLine>();
    public DbSet<DemoAccount> DemoAccounts => Set<DemoAccount>(); public DbSet<Transfer> Transfers => Set<Transfer>(); public DbSet<LedgerEntry> LedgerEntries => Set<LedgerEntry>(); public DbSet<Beneficiary> Beneficiaries => Set<Beneficiary>();
    public DbSet<DocumentRecord> Documents => Set<DocumentRecord>(); public DbSet<DocumentVersion> DocumentVersions => Set<DocumentVersion>(); public DbSet<ApprovalRequest> ApprovalRequests => Set<ApprovalRequest>(); public DbSet<ApprovalAction> ApprovalActions => Set<ApprovalAction>();
    public DbSet<Site> Sites => Set<Site>(); public DbSet<InspectionTemplate> InspectionTemplates => Set<InspectionTemplate>(); public DbSet<InspectionQuestion> InspectionQuestions => Set<InspectionQuestion>(); public DbSet<Inspection> Inspections => Set<Inspection>(); public DbSet<InspectionAnswer> InspectionAnswers => Set<InspectionAnswer>(); public DbSet<Finding> Findings => Set<Finding>();
    public DbSet<LegacyRequest> LegacyRequests => Set<LegacyRequest>(); public DbSet<MigrationChange> MigrationChanges => Set<MigrationChange>();
    public DbSet<Department> Departments => Set<Department>(); public DbSet<Employee> Employees => Set<Employee>(); public DbSet<AttendanceEntry> AttendanceEntries => Set<AttendanceEntry>(); public DbSet<LeaveRequest> LeaveRequests => Set<LeaveRequest>(); public DbSet<ApprovalEvent> ApprovalEvents => Set<ApprovalEvent>(); public DbSet<AuditEvent> AuditEvents => Set<AuditEvent>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Product>().HasIndex(x => x.Sku).IsUnique();
        modelBuilder.Entity<Warehouse>().HasIndex(x => x.Code).IsUnique();
        modelBuilder.Entity<InventoryBalance>().HasIndex(x => new { x.ProductId, x.WarehouseId }).IsUnique();
        modelBuilder.Entity<Transfer>().HasIndex(x => x.IdempotencyKey).IsUnique();
        modelBuilder.Entity<DemoAccount>().HasIndex(x => x.Code).IsUnique();
        modelBuilder.Entity<LegacyRequest>().HasIndex(x => x.Number).IsUnique();
        modelBuilder.Entity<Employee>().HasIndex(x => x.Code).IsUnique();
        modelBuilder.Entity<InventoryBalance>().Property(x => x.Version).IsConcurrencyToken();
        modelBuilder.Entity<Product>().Property(x => x.Price).HasPrecision(18, 2);
        modelBuilder.Entity<Product>().Property(x => x.Cost).HasPrecision(18, 2);
        modelBuilder.Entity<Transfer>().Property(x => x.Amount).HasPrecision(18, 2);
        modelBuilder.Entity<LedgerEntry>().Property(x => x.Amount).HasPrecision(18, 2);
        base.OnModelCreating(modelBuilder);
    }
}
