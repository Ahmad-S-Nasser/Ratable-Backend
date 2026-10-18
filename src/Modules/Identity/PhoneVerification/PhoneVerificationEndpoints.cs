using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Ratable.Modules.Identity.Services;
using Ratable.Shared.Auth;

namespace Ratable.Modules.Identity.PhoneVerification;

public sealed record StartVerifyBody(string? Phone, string? Route, string? Channel);
public sealed record ConfirmVerifyBody(string? Code);
public sealed record StartVerifyResponse(string Route, string? Code, string? WhatsAppUrl, DateTime ExpiresAt);
public sealed record VerifyStatusResponse(string State, DateTime? ExpiresAt);
public sealed record VerifyErrorBody(string Error, int? RetryAfterSeconds = null);

/// <summary>Verify via WhatsApp: start, confirm (code route only), status, and the WhatsApp Cloud API webhook.</summary>
public static class PhoneVerificationEndpoints
{
    public static IEndpointRouteBuilder MapPhoneVerificationEndpoints(this IEndpointRouteBuilder app)
    {
        var me = app.MapGroup("/api/me/phone/verify").WithTags("Phone verification").RequireAuthorization(PolicyNames.SignedIn);
        me.MapPost("/start", Start).Produces<StartVerifyResponse>().Produces<VerifyErrorBody>(400).Produces<VerifyErrorBody>(409).Produces<VerifyErrorBody>(429).Produces<VerifyErrorBody>(503);
        me.MapPost("/confirm", Confirm).Produces<VerifyStatusResponse>().Produces<VerifyErrorBody>(400).Produces<VerifyErrorBody>(429);
        me.MapGet("/status", Status).Produces<VerifyStatusResponse>();

        // The webhook is called by WhatsApp, not by a browser: no session, but every call is checked against the app secret.
        var webhook = app.MapGroup("/api/webhooks/whatsapp").WithTags("Phone verification");
        webhook.MapGet("", Challenge).Produces(200).Produces(403);
        webhook.MapPost("", Receive).Produces(200).Produces(401).Produces(503);
        return app;
    }

    private static string UserId(ClaimsPrincipal p) => p.FindFirstValue(ClaimTypes.NameIdentifier)!;

    private static string ErrorCode(VerifyFailure f) => f switch
    {
        VerifyFailure.InvalidPhone => "invalid_phone",
        VerifyFailure.NumberNotAllowed => "number_not_allowed",
        VerifyFailure.PhoneInUse => "phone_in_use",
        VerifyFailure.AlreadyVerified => "already_verified",
        VerifyFailure.RateLimited => "rate_limited",
        VerifyFailure.TooManyAccountsOnDevice => "too_many_accounts_on_device",
        VerifyFailure.NotStarted => "not_started",
        VerifyFailure.CodeExpired => "code_expired",
        VerifyFailure.InvalidCode => "invalid_code",
        VerifyFailure.TooManyAttempts => "too_many_attempts",
        VerifyFailure.WrongSender => "wrong_sender",
        VerifyFailure.ProviderUnavailable => "provider_unavailable",
        _ => "unknown",
    };

    private static IResult Fail(PhoneVerifyStart r) => r.Failure switch
    {
        VerifyFailure.RateLimited => Results.Json(new VerifyErrorBody("rate_limited", r.RetryAfterSeconds), statusCode: StatusCodes.Status429TooManyRequests),
        VerifyFailure.PhoneInUse or VerifyFailure.AlreadyVerified or VerifyFailure.TooManyAccountsOnDevice => Results.Conflict(new VerifyErrorBody(ErrorCode(r.Failure))),
        VerifyFailure.ProviderUnavailable => Results.Json(new VerifyErrorBody("provider_unavailable"), statusCode: StatusCodes.Status503ServiceUnavailable),
        _ => Results.BadRequest(new VerifyErrorBody(ErrorCode(r.Failure))),
    };

    private static async Task<IResult> Start(
        StartVerifyBody body, ClaimsPrincipal user, HttpContext http, IEnumerable<IPhoneVerifier> verifiers, IOptions<IdentityOptions> options, CancellationToken ct)
    {
        // The reverse route is the default (no per-message cost). The other route is used only when asked for.
        var wanted = string.Equals(body.Route, "code", StringComparison.OrdinalIgnoreCase) ? VerifyRoute.Code : VerifyRoute.Reverse;
        var verifier = verifiers.FirstOrDefault(v => v.Route == wanted);
        if (verifier is null) return Results.Json(new VerifyErrorBody("provider_unavailable"), statusCode: StatusCodes.Status503ServiceUnavailable);

        var context = new DeviceContext(http.Request.Headers["X-Device-Id"].ToString().Trim(), http.Connection.RemoteIpAddress?.ToString());
        var result = await verifier.StartAsync(UserId(user), body.Phone, context, body.Channel, ct);
        return result.Ok
            ? Results.Ok(new StartVerifyResponse(result.Route == VerifyRoute.Reverse ? "reverse" : "code", result.Code, result.WhatsAppUrl, result.ExpiresAt!.Value))
            : Fail(result);
    }

    private static async Task<IResult> Confirm(ConfirmVerifyBody body, ClaimsPrincipal user, PhoneVerificationService service, CancellationToken ct)
    {
        var result = await service.ConfirmCodeAsync(UserId(user), body.Code, ct);
        if (result.Ok) return Results.Ok(new VerifyStatusResponse("verified", null));
        return result.Failure == VerifyFailure.TooManyAttempts
            ? Results.Json(new VerifyErrorBody("too_many_attempts"), statusCode: StatusCodes.Status429TooManyRequests)
            : Results.BadRequest(new VerifyErrorBody(ErrorCode(result.Failure)));
    }

    private static async Task<IResult> Status(ClaimsPrincipal user, PhoneVerificationService service, CancellationToken ct)
    {
        var status = await service.StatusAsync(UserId(user), ct);
        return Results.Ok(new VerifyStatusResponse(status.State, status.ExpiresAt));
    }

    // ---- the WhatsApp Cloud API webhook ----

    /// <summary>Meta calls this once when the webhook is set up: it must echo the challenge when the verify token matches.</summary>
    private static IResult Challenge(HttpRequest request, IOptions<IdentityOptions> options)
    {
        var token = options.Value.WhatsAppVerifyToken;
        var ok = !string.IsNullOrEmpty(token) && request.Query["hub.mode"] == "subscribe" && request.Query["hub.verify_token"] == token;
        return ok ? Results.Text(request.Query["hub.challenge"].ToString()) : Results.StatusCode(StatusCodes.Status403Forbidden);
    }

    private static async Task<IResult> Receive(HttpRequest request, PhoneVerificationService service, IOptions<IdentityOptions> options, ILoggerFactory loggers, CancellationToken ct)
    {
        var secret = options.Value.WhatsAppAppSecret;
        if (string.IsNullOrEmpty(secret)) return Results.StatusCode(StatusCodes.Status503ServiceUnavailable); // never accept unsigned messages

        using var buffer = new MemoryStream();
        await request.Body.CopyToAsync(buffer, ct);
        var body = buffer.ToArray();
        if (!SignatureMatches(secret, body, request.Headers["X-Hub-Signature-256"].ToString())) return Results.StatusCode(StatusCodes.Status401Unauthorized);

        try
        {
            using var json = JsonDocument.Parse(body);
            if (json.RootElement.TryGetProperty("entry", out var entries))
                foreach (var entry in entries.EnumerateArray())
                    if (entry.TryGetProperty("changes", out var changes))
                        foreach (var change in changes.EnumerateArray())
                            if (change.TryGetProperty("value", out var value) && value.TryGetProperty("messages", out var messages))
                                foreach (var message in messages.EnumerateArray())
                                {
                                    var from = message.TryGetProperty("from", out var f) ? f.GetString() : null;
                                    var text = message.TryGetProperty("text", out var t) && t.TryGetProperty("body", out var b) ? b.GetString() : null;
                                    if (from is not null) await service.HandleIncomingMessageAsync(from, text, ct);
                                }
        }
        catch (JsonException)
        {
            loggers.CreateLogger("WhatsAppWebhook").LogWarning("A signed WhatsApp webhook call had a body that is not JSON");
        }
        // WhatsApp expects a quick 200 whatever happened to a message; the person's screen learns the result from the status call.
        return Results.Ok();
    }

    private static bool SignatureMatches(string secret, byte[] body, string header)
    {
        const string prefix = "sha256=";
        if (!header.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return false;
        var expected = Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), body)).ToLowerInvariant();
        return CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(expected), Encoding.UTF8.GetBytes(header[prefix.Length..].ToLowerInvariant()));
    }
}
