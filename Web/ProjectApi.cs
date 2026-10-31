using System.Security.Claims;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Preparation.Shared.Data;

namespace Preparation.Shared.Web;

public static class ProjectApi
{
    public static void Map(WebApplication app, ProjectInfo info)
    {
        switch (info.Key)
        {
            case "helpdesk": HelpDeskApi.Map(app); break;
            case "inventory": InventoryApi.Map(app); break;
            case "wallet": WalletApi.Map(app); break;
            case "documents": DocumentsApi.Map(app); break;
            case "inspections": InspectionsApi.Map(app); break;
            case "legacy": LegacyApi.Map(app); break;
            case "hr": HrApi.Map(app); break;
        }
    }
}

internal static class ApiHelpers
{
    public static string User(ClaimsPrincipal user) => user.Identity?.Name ?? "anonymous";
    public static bool InRole(ClaimsPrincipal user, params string[] roles) => roles.Any(user.IsInRole);
    public static RouteHandlerBuilder Roles(this RouteHandlerBuilder endpoint, params string[] roles) => endpoint.RequireAuthorization(policy => policy.RequireRole(roles));
    public static async Task SaveAuditAsync(AppDbContext db, string actor, string action, string entity, object id, string details = "")
    {
        db.AuditEvents.Add(new Preparation.Shared.Domain.AuditEvent { Actor = actor, Action = action, Entity = entity, EntityId = id.ToString() ?? "", Details = details.Length > 1000 ? details[..1000] : details });
        await db.SaveChangesAsync();
    }
}

public sealed record TicketCreate(string? Title, string? Description, string? Category, string? Priority);
public sealed record StatusChange(string? Status, string? Comment);
public sealed record CommentCreate(string? Text, bool Internal = false);
public sealed record AssignmentChange(string? Agent);
public sealed record ProductCreate(string? Sku, string? Name, string? Unit, decimal Cost, decimal Price, decimal MinStock);
public sealed record PurchaseCreate(string? Supplier, int ProductId, decimal Quantity, decimal UnitCost);
public sealed record ReceiveCreate(decimal Quantity);
public sealed record SaleLineInput(int ProductId, decimal Quantity);
public sealed record SaleCreate(int WarehouseId, List<SaleLineInput>? Lines);
public sealed record TransferCreate(int SourceAccountId, int DestinationAccountId, decimal Amount, string? IdempotencyKey);
public sealed record DocumentCreate(string? Title, string? Description, string? Category, string? Confidentiality);
public sealed record ApprovalDecision(string? Decision, string? Comment);
public sealed record InspectionCreate(int SiteId, int TemplateId, string? Inspector);
public sealed record AnswerInput(int QuestionId, string? Value, string? Comment);
public sealed record AnswersUpdate(List<AnswerInput>? Answers);
public sealed record FindingClose(string? Comment);
public sealed record LegacyCreate(string? Title, string? Description, string? Priority);
public sealed record LegacyUpdate(string? Title, string? Description, string? State, string? Priority);
public sealed record AttendanceCreate(int EmployeeId, string? Type, DateOnly? Date, TimeOnly? Time);
public sealed record LeaveCreate(int EmployeeId, string? Type, DateOnly From, DateOnly To, string? Reason);
public sealed record LeaveDecision(string? Decision, string? Comment);
