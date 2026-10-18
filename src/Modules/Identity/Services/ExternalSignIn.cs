using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace Ratable.Modules.Identity.Services;

/// <summary>Who an identity provider says someone is: its name for the provider and its stable subject id. No email, no name, nothing else is kept.</summary>
public sealed record ExternalIdentity(string Provider, string Subject);

/// <summary>Checks an ID token from Google or Apple. Anything that is not clearly valid gives null.</summary>
public interface IExternalIdTokenVerifier
{
    /// <summary>"google" or "apple".</summary>
    string Provider { get; }

    Task<ExternalIdentity?> VerifyAsync(string idToken, CancellationToken cancellationToken = default);
}

/// <summary>
/// Validates an OpenID Connect ID token the way both Google and Apple require: a genuine signature from the provider's published keys, the expected
/// issuer, this app's client id as the audience, and a lifetime that has not run out. The provider's keys come from its discovery document.
/// </summary>
public sealed class OidcIdTokenVerifier(
    string provider, IEnumerable<string> issuers, string audience, IConfigurationManager<OpenIdConnectConfiguration> configuration, TimeProvider time) : IExternalIdTokenVerifier
{
    public string Provider => provider;

    public async Task<ExternalIdentity?> VerifyAsync(string idToken, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(idToken) || idToken.Length > 8192) return null;
        try
        {
            var config = await configuration.GetConfigurationAsync(ct);
            var now = time.GetUtcNow().UtcDateTime;
            var parameters = new TokenValidationParameters
            {
                ValidIssuers = issuers,
                ValidAudience = audience,
                IssuerSigningKeys = config.SigningKeys,
                ValidateIssuer = true,
                ValidateAudience = true,
                ValidateIssuerSigningKey = true,
                RequireSignedTokens = true,
                RequireExpirationTime = true,
                ValidateLifetime = true,
                // The clock is the application's own, so tests can move it and the rules are the same everywhere.
                LifetimeValidator = (notBefore, expires, _, _) => expires is { } e && e > now.AddSeconds(-60) && (notBefore is not { } nb || nb < now.AddSeconds(60)),
                ValidAlgorithms = [SecurityAlgorithms.RsaSha256, SecurityAlgorithms.RsaSha384, SecurityAlgorithms.RsaSha512, SecurityAlgorithms.EcdsaSha256],
            };
            var result = await new JsonWebTokenHandler().ValidateTokenAsync(idToken, parameters);
            if (!result.IsValid) return null;
            var subject = result.ClaimsIdentity.FindFirst("sub")?.Value;
            return string.IsNullOrWhiteSpace(subject) ? null : new ExternalIdentity(provider, subject);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null; // a provider outage or a malformed token is a refusal, never a sign-in
        }
    }
}
