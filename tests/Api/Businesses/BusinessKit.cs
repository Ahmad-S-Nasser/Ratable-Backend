using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using Ratable.Api.Tests.Identity;
using Ratable.Modules.Businesses.Data;
using Ratable.Modules.Businesses.Models;
using static Ratable.Api.Tests.Moderation.ModerationKit;
using static Ratable.Api.Tests.TestKit;

namespace Ratable.Api.Tests.Businesses;

/// <summary>Set-up shared by the business tests: an admin, a business account with a verified phone, a place, and the claim steps.</summary>
public static class BusinessKit
{
    public static async Task<IdentityApiFactory.SignedIn> Business(IdentityApiFactory api, string phone = "+201555555555", bool verified = true)
    {
        api.Time.Advance(TimeSpan.FromMinutes(2));
        var user = await api.SignInAsync(phone);
        if (verified) await VerifyPhone(api, user);
        return user;
    }

    /// <summary>Makes an account the owner of a place the way an approved claim does: the ownership row and the role.</summary>
    public static async Task MakeOwner(IdentityApiFactory api, IdentityApiFactory.SignedIn user, string entityId)
    {
        await api.Services.GetRequiredService<OwnershipRepository>().TryInsertAsync(new BusinessOwnership { EntityId = entityId, UserId = IdOf(user), ClaimId = NewId() });
        var granter = api.Services.GetRequiredService<Ratable.Shared.Businesses.IBusinessRoleGranter>();
        await granter.SetBusinessOwnerAsync(IdOf(user), true);
    }

    /// <summary>The newest code mailed to an address, read from the test mail folder. Null when nothing was sent.</summary>
    public static string? MailedCode(IdentityApiFactory api, string to)
    {
        if (!Directory.Exists(api.MailDirectory)) return null;
        foreach (var file in new DirectoryInfo(api.MailDirectory).GetFiles("*.txt").OrderByDescending(f => f.Name))
        {
            var text = File.ReadAllText(file.FullName);
            if (!text.StartsWith($"To: {to}\n", StringComparison.Ordinal)) continue;
            return Regex.Match(text, @"code is (\d{6})").Groups[1].Value;
        }
        return null;
    }

    public static Task<(HttpResponseMessage Response, JsonElement Json)> ConfirmEmail(IdentityApiFactory api, string cookie, string claimId, string? code) =>
        Send(api, HttpMethod.Post, $"/api/me/claims/{claimId}/email/confirm", cookie, new { code });

    public static Task<(HttpResponseMessage Response, JsonElement Json)> Claim(IdentityApiFactory api, string cookie, string entityId, string email = "owner@madinaty-co.com", string role = "owner") =>
        Send(api, HttpMethod.Post, $"/api/entities/{entityId}/claims", cookie, new { companyEmail = email, role, website = "https://madinaty-co.com", note = "I run the place" });

    public static async Task<(HttpResponseMessage Response, JsonElement Json)> Upload(IdentityApiFactory api, string cookie, string claimId, byte[] file, string type = "commercial_register")
    {
        using var http = api.CreateClient();
        var request = new HttpRequestMessage(HttpMethod.Put, $"/api/me/claims/{claimId}/document?type={type}") { Content = new ByteArrayContent(file) };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        request.Headers.Add("Cookie", cookie.Split(';')[0]);
        var response = await http.SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();
        return (response, text.TrimStart().StartsWith('{') ? JsonSerializer.Deserialize<JsonElement>(text) : default);
    }

    /// <summary>A claim that has its document and waits for a moderator. Returns the claim id.</summary>
    public static async Task<string> PendingClaim(IdentityApiFactory api, IdentityApiFactory.SignedIn user, string entityId, string email = "owner@madinaty-co.com", int seed = 1)
    {
        var (created, json) = await Claim(api, user.Cookie!, entityId, email);
        Xunit.Assert.Equal(System.Net.HttpStatusCode.Created, created.StatusCode);
        var id = json.GetProperty("id").GetString()!;
        Xunit.Assert.Equal(System.Net.HttpStatusCode.OK, (await ConfirmEmail(api, user.Cookie!, id, MailedCode(api, email))).Response.StatusCode);
        Xunit.Assert.Equal(System.Net.HttpStatusCode.OK, (await Upload(api, user.Cookie!, id, Png(seed))).Response.StatusCode);
        return id;
    }
}
