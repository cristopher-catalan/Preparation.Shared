using System.Security.Claims;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Preparation.Shared.Data;
using Preparation.Shared.Domain;

namespace Preparation.Shared.Web;

internal static class LegacyApi
{
    public static void Map(WebApplication app)
    {
        var api = app.MapGroup("/api").RequireAuthorization();
        api.MapGet("/requests", async (AppDbContext db, string? state, string? q, int page = 1, int pageSize = 20) =>
        {
            page = Math.Clamp(page, 1, 500); pageSize = Math.Clamp(pageSize, 1, 100); var query = db.LegacyRequests.AsQueryable();
            if (!string.IsNullOrWhiteSpace(state)) query = query.Where(x => x.State == state); if (!string.IsNullOrWhiteSpace(q)) query = query.Where(x => x.Number.Contains(q) || x.Title.Contains(q));
            return Results.Ok(new { total = await query.CountAsync(), items = await query.OrderBy(x => x.Number).Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(), page, pageSize });
        });
        api.MapPost("/requests", async (LegacyCreate input, AppDbContext db, ClaimsPrincipal user) =>
        {
            if (string.IsNullOrWhiteSpace(input.Title) || input.Title.Length > 180 || string.IsNullOrWhiteSpace(input.Description)) return Results.BadRequest(new { error = "Título y descripción son obligatorios." });
            var next = (await db.LegacyRequests.Select(x => (int?)x.Id).MaxAsync() ?? 0) + 1;
            var item = new LegacyRequest { Number = $"LEG-{next:0000}", Title = input.Title.Trim(), Description = input.Description.Trim(), Priority = input.Priority is "Baja" or "Normal" or "Alta" ? input.Priority : "Normal" };
            db.LegacyRequests.Add(item); await db.SaveChangesAsync(); await ApiHelpers.SaveAuditAsync(db, ApiHelpers.User(user), "Solicitud legacy creada", "LegacyRequest", item.Id); return Results.Created($"/api/requests/{item.Id}", item);
        }).Roles("Desarrollador", "Administrador");
        api.MapGet("/requests/{id:int}", async (int id, AppDbContext db) => { var item = await db.LegacyRequests.FindAsync(id); return item is null ? Results.NotFound() : Results.Ok(item); });
        api.MapPut("/requests/{id:int}", async (int id, LegacyUpdate input, AppDbContext db, ClaimsPrincipal user) =>
        {
            var item = await db.LegacyRequests.FindAsync(id); if (item is null) return Results.NotFound();
            if (string.IsNullOrWhiteSpace(input.Title) || string.IsNullOrWhiteSpace(input.Description) || input.State is not ("Abierta" or "En proceso" or "Resuelta" or "Cerrada") || input.Priority is not ("Baja" or "Normal" or "Alta")) return Results.BadRequest(new { error = "Revisa título, descripción, estado o prioridad." });
            var before = $"{item.State}/{item.Priority}"; item.Title = input.Title.Trim(); item.Description = input.Description.Trim(); item.State = input.State!; item.Priority = input.Priority!; item.UpdatedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(); await ApiHelpers.SaveAuditAsync(db, ApiHelpers.User(user), "Solicitud actualizada", "LegacyRequest", id, before + " -> " + item.State + "/" + item.Priority); return Results.Ok(item);
        }).Roles("Desarrollador", "Administrador");
        api.MapGet("/migration/status", async (AppDbContext db) => Results.Ok(new { demo = true, modules = new[] { new { name = "Solicitudes", status = "Baseline disponible" }, new { name = "Persistencia EF Core", status = "Pendiente" }, new { name = "API moderna", status = "En preparación" }, new { name = "Despliegue y reversión", status = "Pendiente" } }, changes = await db.MigrationChanges.OrderByDescending(x => x.At).ToListAsync() }));
        api.MapPost("/migration/changes", async (MigrationChange input, AppDbContext db, ClaimsPrincipal user) =>
        {
            if (string.IsNullOrWhiteSpace(input.Module) || string.IsNullOrWhiteSpace(input.Change) || string.IsNullOrWhiteSpace(input.Rollback)) return Results.BadRequest(new { error = "Módulo, cambio y estrategia de reversión son obligatorios." });
            input.Id = 0; input.Actor = ApiHelpers.User(user); input.At = DateTimeOffset.UtcNow; db.MigrationChanges.Add(input); await db.SaveChangesAsync(); return Results.Created($"/api/migration/changes/{input.Id}", input);
        }).Roles("Desarrollador", "Administrador");
        api.MapPost("/characterization/run", (CharacterizationInput input) =>
        {
            var source = input.Value ?? ""; var legacy = source.Trim().ToUpperInvariant(); var modern = source.Trim().ToUpperInvariant();
            return Results.Ok(new { input = source, legacyOutput = legacy, modernOutput = modern, matches = legacy == modern, note = "Caso sintético de caracterización; no corresponde a un sistema de cliente." });
        }).Roles("Desarrollador", "Auditor", "Administrador");
    }
}

internal static class HrApi
{
    public static void Map(WebApplication app)
    {
        var api = app.MapGroup("/api").RequireAuthorization();
        api.MapGet("/employees", async (AppDbContext db, ClaimsPrincipal user, string? department, string? q) =>
        {
            var query = db.Employees.AsQueryable();
            if (user.IsInRole("Empleado")) query = query.Where(x => x.Code == (ApiHelpers.User(user) == "employee" ? "EMP-001" : ""));
            if (user.IsInRole("Supervisor")) query = query.Where(x => x.Supervisor == ApiHelpers.User(user));
            if (!string.IsNullOrWhiteSpace(department)) query = query.Where(x => x.Department == department);
            if (!string.IsNullOrWhiteSpace(q)) query = query.Where(x => x.Name.Contains(q) || x.Code.Contains(q));
            return Results.Ok(await query.OrderBy(x => x.Name).ToListAsync());
        });
        api.MapGet("/attendance", async (AppDbContext db, ClaimsPrincipal user, int? employeeId, DateOnly? from, DateOnly? to) =>
        {
            var query = db.AttendanceEntries.AsQueryable();
            if (user.IsInRole("Empleado")) query = query.Where(x => x.EmployeeId == 1);
            if (user.IsInRole("Supervisor")) { var allowed = await db.Employees.Where(x => x.Supervisor == ApiHelpers.User(user)).Select(x => x.Id).ToListAsync(); query = query.Where(x => allowed.Contains(x.EmployeeId)); }
            if (employeeId.HasValue && !user.IsInRole("Empleado")) query = query.Where(x => x.EmployeeId == employeeId.Value);
            if (from.HasValue) query = query.Where(x => x.Date >= from.Value); if (to.HasValue) query = query.Where(x => x.Date <= to.Value);
            return Results.Ok(await query.OrderByDescending(x => x.Date).ThenByDescending(x => x.Time).Take(500).ToListAsync());
        });
        api.MapPost("/attendance", async (AttendanceCreate input, AppDbContext db, ClaimsPrincipal user) =>
        {
            var employeeId = user.IsInRole("Empleado") ? 1 : input.EmployeeId;
            if (user.IsInRole("Empleado") && input.EmployeeId != 1) return Results.Forbid();
            var employee = await db.Employees.FindAsync(employeeId); if (employee is null || !employee.Active) return Results.BadRequest(new { error = "Empleado activo no encontrado." });
            if (user.IsInRole("Supervisor") && employee.Supervisor != ApiHelpers.User(user)) return Results.Forbid();
            if (input.Type is not ("Entrada" or "Salida")) return Results.BadRequest(new { error = "El tipo debe ser Entrada o Salida." });
            var date = input.Date ?? DateOnly.FromDateTime(DateTime.Now); var time = input.Time ?? TimeOnly.FromDateTime(DateTime.Now);
            var last = await db.AttendanceEntries.Where(x => x.EmployeeId == employeeId && x.Date == date).OrderByDescending(x => x.Time).FirstOrDefaultAsync();
            if (last?.Type == input.Type) return Results.Conflict(new { error = $"La última marcación del día ya es {last.Type}." });
            if (last is not null && time <= last.Time) return Results.Conflict(new { error = "La hora de la nueva marcación debe ser posterior a la anterior." });
            if (input.Type == "Salida" && last is null) return Results.Conflict(new { error = "Registra una entrada antes de la salida." });
            var entry = new AttendanceEntry { EmployeeId = employeeId, Date = date, Time = time, Type = input.Type!, Actor = ApiHelpers.User(user) };
            db.AttendanceEntries.Add(entry); await db.SaveChangesAsync(); await ApiHelpers.SaveAuditAsync(db, ApiHelpers.User(user), "Marcación registrada", "AttendanceEntry", entry.Id, input.Type!); return Results.Created($"/api/attendance/{entry.Id}", entry);
        }).Roles("Empleado", "Supervisor", "RecursosHumanos", "Administrador");
        api.MapGet("/leave-requests", async (AppDbContext db, ClaimsPrincipal user, string? state) =>
        {
            var query = db.LeaveRequests.AsQueryable();
            if (user.IsInRole("Empleado")) query = query.Where(x => x.EmployeeId == 1);
            if (user.IsInRole("Supervisor")) { var allowed = await db.Employees.Where(x => x.Supervisor == ApiHelpers.User(user)).Select(x => x.Id).ToListAsync(); query = query.Where(x => allowed.Contains(x.EmployeeId)); }
            if (!string.IsNullOrWhiteSpace(state)) query = query.Where(x => x.State == state);
            return Results.Ok(await query.OrderByDescending(x => x.CreatedAt).ToListAsync());
        });
        api.MapPost("/leave-requests", async (LeaveCreate input, AppDbContext db, ClaimsPrincipal user) =>
        {
            var employeeId = user.IsInRole("Empleado") ? 1 : input.EmployeeId; if (user.IsInRole("Empleado") && input.EmployeeId != 1) return Results.Forbid();
            var employee = await db.Employees.FindAsync(employeeId); if (employee is null || !employee.Active) return Results.BadRequest(new { error = "Empleado activo no encontrado." });
            if (input.From > input.To || input.To.DayNumber - input.From.DayNumber > 60 || string.IsNullOrWhiteSpace(input.Reason) || input.Reason.Length > 1000) return Results.BadRequest(new { error = "Rango o motivo no válido; máximo 60 días." });
            var overlap = await db.LeaveRequests.AnyAsync(x => x.EmployeeId == employeeId && x.State != "Rechazada" && x.From <= input.To && x.To >= input.From);
            if (overlap) return Results.Conflict(new { error = "Ya existe una solicitud pendiente que se cruza con esas fechas." });
            var item = new LeaveRequest { EmployeeId = employeeId, Type = input.Type is "Vacaciones" or "Permiso" or "Descanso médico" ? input.Type! : "Permiso", From = input.From, To = input.To, Reason = input.Reason.Trim() };
            db.LeaveRequests.Add(item); await db.SaveChangesAsync(); await ApiHelpers.SaveAuditAsync(db, ApiHelpers.User(user), "Solicitud de ausencia creada", "LeaveRequest", item.Id); return Results.Created($"/api/leave-requests/{item.Id}", item);
        }).Roles("Empleado", "Supervisor", "RecursosHumanos", "Administrador");
        api.MapPost("/leave-requests/{id:int}/decision", async (int id, LeaveDecision input, AppDbContext db, ClaimsPrincipal user) =>
        {
            if (input.Decision is not ("Aprobar" or "Rechazar")) return Results.BadRequest(new { error = "La decisión debe ser Aprobar o Rechazar." });
            var item = await db.LeaveRequests.FindAsync(id); if (item is null) return Results.NotFound(); if (item.State != "Pendiente") return Results.Conflict(new { error = "La solicitud ya fue decidida." });
            var employee = await db.Employees.FindAsync(item.EmployeeId); if (user.IsInRole("Supervisor") && employee?.Supervisor != ApiHelpers.User(user)) return Results.Forbid();
            item.State = input.Decision == "Aprobar" ? "Aprobada" : "Rechazada"; item.Approver = ApiHelpers.User(user);
            db.ApprovalEvents.Add(new ApprovalEvent { LeaveRequestId = id, Actor = ApiHelpers.User(user), Decision = input.Decision!, Comment = input.Comment?.Trim() ?? "" });
            await db.SaveChangesAsync(); await ApiHelpers.SaveAuditAsync(db, ApiHelpers.User(user), input.Decision!, "LeaveRequest", id); return Results.Ok(item);
        }).Roles("Supervisor", "RecursosHumanos", "Administrador");
        api.MapGet("/reports/attendance.csv", async (AppDbContext db, DateOnly? from, DateOnly? to) =>
        {
            var start = from ?? DateOnly.FromDateTime(DateTime.Today.AddDays(-30)); var end = to ?? DateOnly.FromDateTime(DateTime.Today);
            var rows = await (from a in db.AttendanceEntries join e in db.Employees on a.EmployeeId equals e.Id where a.Date >= start && a.Date <= end orderby a.Date, e.Code, a.Time select new { e.Code, e.Name, a.Date, a.Time, a.Type }).ToListAsync();
            var csv = new StringBuilder("Codigo,Empleado,Fecha,Hora,Tipo\r\n"); foreach (var row in rows) csv.AppendLine($"{Csv(row.Code)},{Csv(row.Name)},{row.Date:yyyy-MM-dd},{row.Time:HH:mm:ss},{Csv(row.Type)}");
            return Results.Text(csv.ToString(), "text/csv; charset=utf-8", Encoding.UTF8);
        }).Roles("Supervisor", "RecursosHumanos", "Administrador");
    }

    private static string Csv(string text) => "\"" + text.Replace("\"", "\"\"") + "\"";
}

public sealed record CharacterizationInput(string? Value);



