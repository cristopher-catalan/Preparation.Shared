using System.Security.Claims;
using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Preparation.Shared.Data;
using Preparation.Shared.Domain;

namespace Preparation.Shared.Web;

internal static class DocumentsApi
{
    private static readonly HashSet<string> AllowedExtensions = new(StringComparer.OrdinalIgnoreCase) { ".pdf", ".docx", ".xlsx", ".txt", ".png", ".jpg", ".jpeg" };
    private const long MaxBytes = 10 * 1024 * 1024;
    public static void Map(WebApplication app)
    {
        var api = app.MapGroup("/api").RequireAuthorization();
        api.MapGet("/documents", async (AppDbContext db, ClaimsPrincipal user, string? state, string? q) =>
        {
            var query = db.Documents.Where(x => x.RetiredAt == null).AsQueryable();
            if (user.IsInRole("Autor")) query = query.Where(x => x.Owner == ApiHelpers.User(user) || x.Confidentiality != "Restringido");
            if (!string.IsNullOrWhiteSpace(state)) query = query.Where(x => x.State == state);
            if (!string.IsNullOrWhiteSpace(q)) query = query.Where(x => x.Title.Contains(q) || x.Category.Contains(q));
            return Results.Ok(await query.OrderByDescending(x => x.CreatedAt).Take(100).ToListAsync());
        });
        api.MapPost("/documents", async (DocumentCreate input, AppDbContext db, ClaimsPrincipal user) =>
        {
            if (string.IsNullOrWhiteSpace(input.Title) || input.Title.Length > 200) return Results.BadRequest(new { error = "El título es obligatorio y debe tener hasta 200 caracteres." });
            var confidentiality = input.Confidentiality is "Público" or "Interno" or "Restringido" ? input.Confidentiality : "Interno";
            var document = new DocumentRecord { Owner = ApiHelpers.User(user), Title = input.Title.Trim(), Description = input.Description?.Trim() ?? "", Category = input.Category?.Trim() is { Length: > 0 } category ? category : "General", Confidentiality = confidentiality };
            db.Documents.Add(document); await db.SaveChangesAsync(); await ApiHelpers.SaveAuditAsync(db, ApiHelpers.User(user), "Documento creado", "Document", document.Id);
            return Results.Created($"/api/documents/{document.Id}", document);
        }).Roles("Autor", "Administrador");
        api.MapGet("/documents/{id:int}", async (int id, AppDbContext db, ClaimsPrincipal user) =>
        {
            var document = await db.Documents.FindAsync(id); if (document is null || document.RetiredAt is not null) return Results.NotFound();
            if (document.Confidentiality == "Restringido" && document.Owner != ApiHelpers.User(user) && !ApiHelpers.InRole(user, "Administrador", "Auditor")) return Results.Forbid();
            return Results.Ok(new { document, versions = await db.DocumentVersions.Where(x => x.DocumentId == id).OrderByDescending(x => x.Number).ToListAsync(), approvals = await db.ApprovalRequests.Where(x => x.DocumentId == id).OrderByDescending(x => x.CreatedAt).ToListAsync() });
        });
        api.MapPost("/documents/{id:int}/versions", async (int id, HttpRequest request, AppDbContext db, IWebHostEnvironment environment, IConfiguration configuration, ClaimsPrincipal user) =>
        {
            var document = await db.Documents.FindAsync(id); if (document is null || document.RetiredAt is not null) return Results.NotFound();
            if (document.Owner != ApiHelpers.User(user) && !user.IsInRole("Administrador")) return Results.Forbid();
            if (!request.HasFormContentType) return Results.BadRequest(new { error = "Envía un archivo como multipart/form-data." });
            var file = request.Form.Files.FirstOrDefault(); if (file is null || file.Length == 0) return Results.BadRequest(new { error = "Selecciona un archivo no vacío." });
            if (file.Length > MaxBytes) return Results.BadRequest(new { error = "El límite por archivo es 10 MB." });
            var extension = Path.GetExtension(Path.GetFileName(file.FileName)); if (!AllowedExtensions.Contains(extension)) return Results.BadRequest(new { error = "Extensión no permitida. Usa PDF, DOCX, XLSX, TXT, PNG o JPG." });
            var stored = $"{Guid.NewGuid():N}{extension.ToLowerInvariant()}"; var folder = configuration["DocumentStorage:Path"] ?? Path.Combine(environment.ContentRootPath, "App_Data", "documents"); Directory.CreateDirectory(folder); var path = Path.Combine(folder, stored);
            await using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { await file.CopyToAsync(stream); await stream.FlushAsync(); }
            var bytes = await File.ReadAllBytesAsync(path);
            if (!HasValidSignature(bytes, extension)) { File.Delete(path); return Results.BadRequest(new { error = "El contenido del archivo no coincide con su extensión permitida." }); }
            var hash = Convert.ToHexString(SHA256.HashData(bytes));
            var version = new DocumentVersion { DocumentId = id, Number = (await db.DocumentVersions.Where(x => x.DocumentId == id).Select(x => (int?)x.Number).MaxAsync() ?? 0) + 1, StoredName = stored, OriginalName = Path.GetFileName(file.FileName), ContentType = ContentTypeFor(extension), Size = file.Length, Sha256 = hash, Author = ApiHelpers.User(user) };
            db.DocumentVersions.Add(version); document.State = "Borrador"; await db.SaveChangesAsync(); await ApiHelpers.SaveAuditAsync(db, ApiHelpers.User(user), "Versión cargada", "DocumentVersion", version.Id, hash);
            return Results.Created($"/api/documents/{id}/versions/{version.Id}", version);
        });
        api.MapGet("/documents/{id:int}/versions", async (int id, AppDbContext db, ClaimsPrincipal user) =>
        {
            var document = await db.Documents.FindAsync(id); if (document is null) return Results.NotFound();
            if (document.Owner != ApiHelpers.User(user) && !ApiHelpers.InRole(user, "Revisor", "Auditor", "Administrador")) return Results.Forbid();
            return Results.Ok(await db.DocumentVersions.Where(x => x.DocumentId == id).OrderByDescending(x => x.Number).ToListAsync());
        });
        api.MapPost("/documents/{id:int}/submit", async (int id, AppDbContext db, ClaimsPrincipal user) =>
        {
            var document = await db.Documents.FindAsync(id); if (document is null || document.RetiredAt is not null) return Results.NotFound();
            if (document.Owner != ApiHelpers.User(user) && !user.IsInRole("Administrador")) return Results.Forbid();
            if (await db.ApprovalRequests.AnyAsync(x => x.DocumentId == id && x.State == "Pendiente")) return Results.Conflict(new { error = "Ya existe una revisión pendiente." });
            var version = await db.DocumentVersions.Where(x => x.DocumentId == id).OrderByDescending(x => x.Number).FirstOrDefaultAsync(); if (version is null) return Results.Conflict(new { error = "Carga una versión antes de solicitar aprobación." });
            var approval = new ApprovalRequest { DocumentId = id, VersionId = version.Id, Requester = ApiHelpers.User(user) }; db.ApprovalRequests.Add(approval); document.State = "En revisión"; await db.SaveChangesAsync();
            await ApiHelpers.SaveAuditAsync(db, ApiHelpers.User(user), "Aprobación solicitada", "ApprovalRequest", approval.Id, $"Versión {version.Number}"); return Results.Created($"/api/approvals/{approval.Id}", approval);
        });
        api.MapPost("/approvals/{id:int}/decision", async (int id, ApprovalDecision input, AppDbContext db, ClaimsPrincipal user) =>
        {
            if (!ApiHelpers.InRole(user, "Revisor", "Administrador")) return Results.Forbid();
            if (input.Decision is not ("Aprobar" or "Devolver")) return Results.BadRequest(new { error = "La decisión debe ser Aprobar o Devolver." });
            var approval = await db.ApprovalRequests.FindAsync(id); if (approval is null) return Results.NotFound(); if (approval.State != "Pendiente") return Results.Conflict(new { error = "La solicitud ya tiene una decisión." });
            approval.State = input.Decision == "Aprobar" ? "Aprobada" : "Devuelta"; approval.DecidedAt = DateTimeOffset.UtcNow;
            var document = await db.Documents.FindAsync(approval.DocumentId); if (document is not null) document.State = approval.State;
            db.ApprovalActions.Add(new ApprovalAction { ApprovalRequestId = id, Actor = ApiHelpers.User(user), Decision = input.Decision!, Comment = input.Comment?.Trim() ?? "" });
            await db.SaveChangesAsync(); await ApiHelpers.SaveAuditAsync(db, ApiHelpers.User(user), input.Decision!, "ApprovalRequest", id); return Results.Ok(approval);
        }).Roles("Revisor", "Administrador");
        api.MapGet("/documents/{id:int}/download/{versionId:int}", async (int id, int versionId, AppDbContext db, IWebHostEnvironment environment, IConfiguration configuration, ClaimsPrincipal user) =>
        {
            var document = await db.Documents.FindAsync(id); if (document is null) return Results.NotFound();
            if (document.Owner != ApiHelpers.User(user) && !ApiHelpers.InRole(user, "Revisor", "Auditor", "Administrador")) return Results.Forbid();
            var version = await db.DocumentVersions.FirstOrDefaultAsync(x => x.Id == versionId && x.DocumentId == id); if (version is null) return Results.NotFound();
            var folder = configuration["DocumentStorage:Path"] ?? Path.Combine(environment.ContentRootPath, "App_Data", "documents"); var path = Path.Combine(folder, version.StoredName); if (!File.Exists(path)) return Results.NotFound();
            return Results.File(path, version.ContentType, version.OriginalName);
        });
        api.MapPost("/documents/{id:int}/retire", async (int id, AppDbContext db, ClaimsPrincipal user) =>
        {
            var document = await db.Documents.FindAsync(id); if (document is null) return Results.NotFound();
            if (!ApiHelpers.InRole(user, "Administrador") && document.Owner != ApiHelpers.User(user)) return Results.Forbid();
            document.RetiredAt = DateTimeOffset.UtcNow; document.State = "Retirado"; await db.SaveChangesAsync(); await ApiHelpers.SaveAuditAsync(db, ApiHelpers.User(user), "Documento retirado", "Document", id); return Results.Ok(document);
        });
    }
    private static bool HasValidSignature(byte[] bytes, string extension) => extension.ToLowerInvariant() switch
    {
        ".pdf" => bytes.Length >= 5 && bytes[0] == 0x25 && bytes[1] == 0x50 && bytes[2] == 0x44 && bytes[3] == 0x46 && bytes[4] == 0x2D,
        ".png" => bytes.Length >= 8 && bytes.Take(8).SequenceEqual(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }),
        ".jpg" or ".jpeg" => bytes.Length >= 3 && bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF,
        ".docx" or ".xlsx" => bytes.Length >= 4 && bytes[0] == 0x50 && bytes[1] == 0x4B && bytes[2] == 0x03 && bytes[3] == 0x04,
        ".txt" => !bytes.Contains((byte)0),
        _ => false
    };

    private static string ContentTypeFor(string extension) => extension.ToLowerInvariant() switch
    {
        ".pdf" => "application/pdf",
        ".docx" => "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
        ".xlsx" => "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
        ".txt" => "text/plain; charset=utf-8",
        ".png" => "image/png",
        ".jpg" or ".jpeg" => "image/jpeg",
        _ => "application/octet-stream"
    };
}

internal static class InspectionsApi
{
    public static void Map(WebApplication app)
    {
        var api = app.MapGroup("/api").RequireAuthorization();
        api.MapGet("/sites", async (AppDbContext db) => Results.Ok(await db.Sites.Where(x => x.Active).ToListAsync()));
        api.MapPost("/sites", async (Site input, AppDbContext db) =>
        {
            if (string.IsNullOrWhiteSpace(input.Code) || string.IsNullOrWhiteSpace(input.Name)) return Results.BadRequest(new { error = "Código y nombre son obligatorios." });
            input.Id = 0; db.Sites.Add(input); await db.SaveChangesAsync(); return Results.Created($"/api/sites/{input.Id}", input);
        }).Roles("Coordinador", "Administrador");
        api.MapGet("/templates", async (AppDbContext db) => Results.Ok(await db.InspectionTemplates.Where(x => x.Active).ToListAsync()));
        api.MapGet("/templates/{id:int}/questions", async (int id, AppDbContext db) => Results.Ok(await db.InspectionQuestions.Where(x => x.TemplateId == id).OrderBy(x => x.Id).ToListAsync()));
        api.MapPost("/inspections", async (InspectionCreate input, AppDbContext db, ClaimsPrincipal user) =>
        {
            if (!await db.Sites.AnyAsync(x => x.Id == input.SiteId && x.Active) || !await db.InspectionTemplates.AnyAsync(x => x.Id == input.TemplateId && x.Active)) return Results.BadRequest(new { error = "Sede o plantilla activa no encontrada." });
            var inspector = string.IsNullOrWhiteSpace(input.Inspector) ? "inspector" : input.Inspector.Trim();
            var item = new Inspection { SiteId = input.SiteId, TemplateId = input.TemplateId, Inspector = inspector };
            db.Inspections.Add(item); await db.SaveChangesAsync(); await ApiHelpers.SaveAuditAsync(db, ApiHelpers.User(user), "Inspección programada", "Inspection", item.Id, inspector); return Results.Created($"/api/inspections/{item.Id}", item);
        }).Roles("Coordinador", "Administrador");
        api.MapGet("/inspections", async (AppDbContext db, ClaimsPrincipal user, string? state) =>
        {
            var query = db.Inspections.AsQueryable(); if (user.IsInRole("Inspector")) query = query.Where(x => x.Inspector == ApiHelpers.User(user)); if (!string.IsNullOrWhiteSpace(state)) query = query.Where(x => x.State == state);
            return Results.Ok(await query.OrderByDescending(x => x.StartedAt).ToListAsync());
        });
        api.MapGet("/inspections/{id:int}", async (int id, AppDbContext db, ClaimsPrincipal user) =>
        {
            var item = await db.Inspections.FindAsync(id); if (item is null) return Results.NotFound(); if (user.IsInRole("Inspector") && item.Inspector != ApiHelpers.User(user)) return Results.Forbid();
            return Results.Ok(new { inspection = item, site = await db.Sites.FindAsync(item.SiteId), template = await db.InspectionTemplates.FindAsync(item.TemplateId), questions = await db.InspectionQuestions.Where(x => x.TemplateId == item.TemplateId).ToListAsync(), answers = await db.InspectionAnswers.Where(x => x.InspectionId == id).ToListAsync(), findings = await db.Findings.Where(x => x.InspectionId == id).ToListAsync() });
        });
        api.MapPut("/inspections/{id:int}/answers", async (int id, AnswersUpdate input, AppDbContext db, ClaimsPrincipal user) =>
        {
            if (!ApiHelpers.InRole(user, "Inspector", "Supervisor", "Administrador")) return Results.Forbid();
            var item = await db.Inspections.FindAsync(id); if (item is null) return Results.NotFound();
            if (item.State is "En revisión" or "Cerrada") return Results.Conflict(new { error = "La inspección ya fue enviada." });
            if (user.IsInRole("Inspector") && item.Inspector != ApiHelpers.User(user)) return Results.Forbid();
            if (input.Answers is null || input.Answers.Count == 0) return Results.BadRequest(new { error = "Incluye respuestas." });
            var questions = await db.InspectionQuestions.Where(x => x.TemplateId == item.TemplateId).ToDictionaryAsync(x => x.Id);
            foreach (var answerInput in input.Answers)
            {
                if (!questions.TryGetValue(answerInput.QuestionId, out var question) || answerInput.Value is not ("Conforme" or "No conforme" or "No aplica")) return Results.BadRequest(new { error = "Pregunta o respuesta no válida.", questionId = answerInput.QuestionId });
                var answer = await db.InspectionAnswers.FirstOrDefaultAsync(x => x.InspectionId == id && x.QuestionId == question.Id);
                if (answer is null) { answer = new InspectionAnswer { InspectionId = id, QuestionId = question.Id }; db.InspectionAnswers.Add(answer); }
                answer.Value = answerInput.Value!; answer.Comment = answerInput.Comment?.Trim() ?? ""; answer.Score = answer.Value == "Conforme" ? question.Points : 0;

            }
            item.State = "En ejecución"; await db.SaveChangesAsync();
            var nonconforming = await db.InspectionAnswers.Where(x => x.InspectionId == id && x.Value == "No conforme").ToListAsync();
            foreach (var answer in nonconforming)
            {
                var question = questions[answer.QuestionId];
                if (!await db.Findings.AnyAsync(x => x.InspectionId == id && x.AnswerId == answer.Id && x.State != "Cerrado"))
                    db.Findings.Add(new Finding { InspectionId = id, AnswerId = answer.Id, Severity = question.Points >= 5 ? "Alta" : "Media", Description = answer.Comment.Length > 0 ? answer.Comment : question.Prompt });
            }
            await db.SaveChangesAsync();
            var allAnswers = await db.InspectionAnswers.Where(x => x.InspectionId == id).ToListAsync(); var totalPoints = questions.Values.Where(x => x.Required).Sum(x => x.Points); var earnedPoints = allAnswers.Where(x => questions.TryGetValue(x.QuestionId, out var question) && question.Required).Sum(x => x.Score);
            item.Score = totalPoints == 0 ? 100 : decimal.Round(earnedPoints / totalPoints * 100, 2); await db.SaveChangesAsync();
            return Results.Ok(new { inspection = item, answers = allAnswers, score = item.Score });
        });
        api.MapPost("/inspections/{id:int}/submit", async (int id, AppDbContext db, ClaimsPrincipal user) =>
        {
            if (!ApiHelpers.InRole(user, "Inspector", "Supervisor", "Administrador")) return Results.Forbid();
            var item = await db.Inspections.FindAsync(id); if (item is null) return Results.NotFound(); if (user.IsInRole("Inspector") && item.Inspector != ApiHelpers.User(user)) return Results.Forbid();
            var questions = await db.InspectionQuestions.Where(x => x.TemplateId == item.TemplateId && x.Required).Select(x => x.Id).ToListAsync(); var answered = await db.InspectionAnswers.Where(x => x.InspectionId == id).Select(x => x.QuestionId).ToListAsync();
            var missing = questions.Except(answered).ToArray(); if (missing.Length > 0) return Results.Conflict(new { error = "Faltan respuestas obligatorias.", missing });
            item.State = "En revisión"; item.SubmittedAt = DateTimeOffset.UtcNow; await db.SaveChangesAsync(); await ApiHelpers.SaveAuditAsync(db, ApiHelpers.User(user), "Inspección enviada", "Inspection", id); return Results.Ok(item);
        });
        api.MapPost("/findings/{id:int}/close", async (int id, FindingClose input, AppDbContext db, ClaimsPrincipal user) =>
        {
            var finding = await db.Findings.FindAsync(id); if (finding is null) return Results.NotFound(); if (!string.IsNullOrWhiteSpace(finding.Severity) && (finding.Severity == "Alta" || finding.Severity == "Crítica") && string.IsNullOrWhiteSpace(input.Comment)) return Results.BadRequest(new { error = "Describe la acción correctiva antes de cerrar un hallazgo crítico." }); if (finding.State == "Cerrado") return Results.Conflict(new { error = "El hallazgo ya fue cerrado." });
            finding.State = "Cerrado"; await db.SaveChangesAsync(); await ApiHelpers.SaveAuditAsync(db, ApiHelpers.User(user), "Hallazgo cerrado", "Finding", id, input.Comment ?? ""); return Results.Ok(finding);
        }).Roles("Supervisor", "Administrador");
    }
}



