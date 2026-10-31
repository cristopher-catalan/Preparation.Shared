using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Preparation.Shared.Data;
using Preparation.Shared.Domain;

namespace Preparation.Shared.Web;

internal static class HelpDeskApi
{
    private static readonly string[] Statuses = ["Nuevo", "Asignado", "En atención", "En espera", "Resuelto", "Cerrado"];
    public static void Map(WebApplication app)
    {
        var api = app.MapGroup("/api/tickets").RequireAuthorization();
        api.MapGet("/", async (AppDbContext db, ClaimsPrincipal user, string? status, string? priority, string? category, string? agent, int page = 1, int pageSize = 20) =>
        {
            page = Math.Clamp(page, 1, 500); pageSize = Math.Clamp(pageSize, 1, 100);
            var query = db.Tickets.AsQueryable();
            if (user.IsInRole("Solicitante")) query = query.Where(x => x.Requester == ApiHelpers.User(user));
            if (!string.IsNullOrWhiteSpace(status)) query = query.Where(x => x.Status == status);
            if (!string.IsNullOrWhiteSpace(priority)) query = query.Where(x => x.Priority == priority);
            if (!string.IsNullOrWhiteSpace(category)) query = query.Where(x => x.Category == category);
            if (!string.IsNullOrWhiteSpace(agent)) query = query.Where(x => x.AssignedTo == agent);
            var total = await query.CountAsync();
            var items = await query.OrderByDescending(x => x.UpdatedAt).Skip((page - 1) * pageSize).Take(pageSize).ToListAsync();
            return Results.Ok(new { items, total, page, pageSize });
        });
        api.MapPost("/", async (TicketCreate input, AppDbContext db, ClaimsPrincipal user) =>
        {
            if (string.IsNullOrWhiteSpace(input.Title) || input.Title.Length > 160 || string.IsNullOrWhiteSpace(input.Description) || input.Description.Length > 5000) return Results.BadRequest(new { error = "Asunto y descripción son obligatorios; máximo 160 y 5000 caracteres." });
            if (input.Priority is not null && !new[] { "Baja", "Normal", "Alta", "Urgente" }.Contains(input.Priority)) return Results.BadRequest(new { error = "Prioridad no válida." });
            var ticket = new Ticket { Number = (await db.Tickets.Select(x => (int?)x.Number).MaxAsync() ?? 1000) + 1, Title = input.Title.Trim(), Description = input.Description.Trim(), Category = string.IsNullOrWhiteSpace(input.Category) ? "General" : input.Category.Trim(), Priority = input.Priority ?? "Normal", Requester = ApiHelpers.User(user) };
            db.Tickets.Add(ticket); await db.SaveChangesAsync(); db.TicketEvents.Add(new TicketEvent { TicketId = ticket.Id, Actor = ApiHelpers.User(user), Kind = "Creación", After = ticket.Status });
            await db.SaveChangesAsync(); return Results.Created($"/api/tickets/{ticket.Id}", ticket);
        }).Roles("Solicitante", "Administrador");
        api.MapGet("/{id:int}", async (int id, AppDbContext db, ClaimsPrincipal user) =>
        {
            var ticket = await db.Tickets.FindAsync(id); if (ticket is null) return Results.NotFound();
            if (user.IsInRole("Solicitante") && ticket.Requester != ApiHelpers.User(user)) return Results.Forbid();
            var comments = await db.TicketComments.Where(x => x.TicketId == id && (!x.IsInternal || user.IsInRole("Agente") || user.IsInRole("Supervisor") || user.IsInRole("Administrador"))).OrderBy(x => x.CreatedAt).ToListAsync();
            var events = await db.TicketEvents.Where(x => x.TicketId == id).OrderBy(x => x.At).ToListAsync();
            return Results.Ok(new { ticket, comments, events });
        });
        api.MapPatch("/{id:int}/status", async (int id, StatusChange input, AppDbContext db, ClaimsPrincipal user) =>
        {
            if (!Statuses.Contains(input.Status ?? "")) return Results.BadRequest(new { error = "Estado no válido." });
            var ticket = await db.Tickets.FindAsync(id); if (ticket is null) return Results.NotFound();
            if (user.IsInRole("Solicitante") && (ticket.Requester != ApiHelpers.User(user) || input.Status != "Cerrado" || ticket.Status != "Resuelto")) return Results.Forbid();
            if (ticket.Status == "Cerrado" && input.Status != "Nuevo" && !ApiHelpers.InRole(user, "Supervisor", "Administrador")) return Results.Conflict(new { error = "Un ticket cerrado solo puede reabrirse por supervisión." });
            if (ticket.Status == "Cerrado" && input.Status != "Nuevo") return Results.Conflict(new { error = "Reabre el ticket a Nuevo antes de atenderlo." });
            var previous = ticket.Status; ticket.Status = input.Status!; ticket.UpdatedAt = DateTimeOffset.UtcNow;
            db.TicketEvents.Add(new TicketEvent { TicketId = id, Actor = ApiHelpers.User(user), Kind = "Cambio de estado", Before = previous, After = ticket.Status });
            if (!string.IsNullOrWhiteSpace(input.Comment)) db.TicketComments.Add(new TicketComment { TicketId = id, Author = ApiHelpers.User(user), Text = input.Comment.Trim(), IsInternal = false });
            await db.SaveChangesAsync(); return Results.Ok(ticket);
        });
        api.MapPost("/{id:int}/comments", async (int id, CommentCreate input, AppDbContext db, ClaimsPrincipal user) =>
        {
            var ticket = await db.Tickets.FindAsync(id); if (ticket is null) return Results.NotFound();
            if (user.IsInRole("Solicitante") && ticket.Requester != ApiHelpers.User(user)) return Results.Forbid();
            if (input.Internal && !ApiHelpers.InRole(user, "Agente", "Supervisor", "Administrador")) return Results.Forbid();
            if (string.IsNullOrWhiteSpace(input.Text) || input.Text.Length > 3000) return Results.BadRequest(new { error = "El comentario debe tener entre 1 y 3000 caracteres." });
            var comment = new TicketComment { TicketId = id, Author = ApiHelpers.User(user), Text = input.Text.Trim(), IsInternal = input.Internal };
            db.TicketComments.Add(comment); await db.SaveChangesAsync(); return Results.Created($"/api/tickets/{id}/comments/{comment.Id}", comment);
        });
        api.MapPost("/{id:int}/assignments", async (int id, AssignmentChange input, AppDbContext db, ClaimsPrincipal user) =>
        {
            var ticket = await db.Tickets.FindAsync(id); if (ticket is null) return Results.NotFound();
            if (string.IsNullOrWhiteSpace(input.Agent) || input.Agent.Length > 80) return Results.BadRequest(new { error = "Indica un agente válido." });
            var previous = ticket.AssignedTo; ticket.AssignedTo = input.Agent.Trim(); if (ticket.Status == "Nuevo") ticket.Status = "Asignado"; ticket.UpdatedAt = DateTimeOffset.UtcNow;
            db.TicketEvents.Add(new TicketEvent { TicketId = id, Actor = ApiHelpers.User(user), Kind = "Asignación", Before = previous, After = ticket.AssignedTo });
            await db.SaveChangesAsync(); return Results.Ok(ticket);
        }).Roles("Supervisor", "Administrador");
        app.MapGet("/api/reports/summary", async (AppDbContext db, ClaimsPrincipal user) =>
        {
            var tickets = db.Tickets.AsQueryable(); if (user.IsInRole("Solicitante")) tickets = tickets.Where(x => x.Requester == ApiHelpers.User(user));
            var rows = await tickets.ToListAsync(); var now = DateTimeOffset.UtcNow;
            return Results.Ok(new { total = rows.Count, abiertos = rows.Count(x => x.Status != "Cerrado" && x.Status != "Resuelto"), vencidos = rows.Count(x => x.Status != "Cerrado" && x.Status != "Resuelto" && now - x.CreatedAt > TimeSpan.FromHours(x.Priority == "Urgente" ? 4 : 24)), resueltos = rows.Count(x => x.Status == "Resuelto" || x.Status == "Cerrado") });
        }).RequireAuthorization();
    }
}


