using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;

namespace Preparation.Shared.Security;

public static class DemoAuth
{
    private static readonly Dictionary<string, (string User, string Role, string Label)[]> Users = new(StringComparer.OrdinalIgnoreCase)
    {
        ["helpdesk"] = [("requester", "Solicitante", "Solicitante"), ("agent", "Agente", "Agente"), ("supervisor", "Supervisor", "Supervisor"), ("admin", "Administrador", "Administrador")],
        ["inventory"] = [("stock", "Almacenero", "Almacenero"), ("seller", "Vendedor", "Vendedor"), ("supervisor", "Supervisor", "Supervisor"), ("admin", "Administrador", "Administrador")],
        ["wallet"] = [("customer", "Usuario", "Usuario demo"), ("receiver", "Usuario", "Destinatario demo"), ("support", "Soporte", "Soporte"), ("admin", "Administrador", "Administrador")],
        ["documents"] = [("author", "Autor", "Autor"), ("reviewer", "Revisor", "Revisor"), ("auditor", "Auditor", "Auditor"), ("admin", "Administrador", "Administrador")],
        ["inspections"] = [("coordinator", "Coordinador", "Coordinador"), ("inspector", "Inspector", "Inspector"), ("supervisor", "Supervisor", "Supervisor"), ("admin", "Administrador", "Administrador")],
        ["legacy"] = [("developer", "Desarrollador", "Desarrollador"), ("auditor", "Auditor", "Auditor"), ("admin", "Administrador", "Administrador")],
        ["hr"] = [("employee", "Empleado", "Empleado"), ("supervisor", "Supervisor", "Supervisor"), ("hr", "RecursosHumanos", "RR. HH."), ("admin", "Administrador", "Administrador")]
    };

    public static void AddServices(IServiceCollection services, string kind)
    {
        services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme).AddCookie(options =>
        {
            options.Cookie.Name = $"prep.{kind}.auth";
            options.Cookie.HttpOnly = true;
            options.Cookie.SameSite = SameSiteMode.Strict;
            options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
            options.SlidingExpiration = true;
            options.ExpireTimeSpan = TimeSpan.FromHours(8);
            options.Events.OnRedirectToLogin = context => { context.Response.StatusCode = StatusCodes.Status401Unauthorized; return Task.CompletedTask; };
            options.Events.OnRedirectToAccessDenied = context => { context.Response.StatusCode = StatusCodes.Status403Forbidden; return Task.CompletedTask; };
        });
        services.AddAuthorization();
    }

    public static void MapEndpoints(WebApplication app, string kind)
    {
        var users = Users[kind];
        app.MapGet("/api/auth/demo-users", () => Results.Ok(users.Select(x => new { username = x.User, role = x.Role, label = x.Label })));
        app.MapGet("/api/auth/me", (ClaimsPrincipal principal) => Results.Ok(new { username = principal.Identity?.Name, role = principal.FindFirst(ClaimTypes.Role)?.Value })).RequireAuthorization();
        app.MapPost("/api/auth/login", async (LoginRequest request, HttpContext context, IConfiguration configuration) =>
        {
            var account = users.FirstOrDefault(x => string.Equals(x.User, request.Username?.Trim(), StringComparison.OrdinalIgnoreCase));
            var expected = configuration["DemoAuth:Password"] ?? "Demo123!";
            if (account.User is null || !PasswordEquals(request.Password ?? "", expected)) return Results.Unauthorized();
            var claims = new[] { new Claim(ClaimTypes.NameIdentifier, account.User), new Claim(ClaimTypes.Name, account.User), new Claim(ClaimTypes.Role, account.Role) };
            var identity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
            await context.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, new ClaimsPrincipal(identity), new AuthenticationProperties { IsPersistent = false, AllowRefresh = true });
            return Results.Ok(new { username = account.User, role = account.Role, message = "Sesión demo iniciada." });
        });
        app.MapPost("/api/auth/logout", async (HttpContext context) => { await context.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme); return Results.Ok(new { message = "Sesión cerrada." }); }).RequireAuthorization();
    }

    private static bool PasswordEquals(string provided, string expected)
    {
        var left = SHA256.HashData(Encoding.UTF8.GetBytes(provided));
        var right = SHA256.HashData(Encoding.UTF8.GetBytes(expected));
        return CryptographicOperations.FixedTimeEquals(left, right);
    }

    public sealed record LoginRequest(string? Username, string? Password);
}
