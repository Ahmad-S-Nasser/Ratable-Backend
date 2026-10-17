using System.Security.Claims;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Ratable.Modules.Identity;
using Ratable.Modules.Identity.Auth;
using Ratable.Modules.Identity.Models;
using Ratable.Modules.Identity.Services;

namespace Ratable.Api.Tests.Identity;

public sealed class PhoneNumberTests
{
    [Theory]
    [InlineData("+201012345678", "+201012345678")]
    [InlineData("01012345678", "+201012345678")]
    [InlineData("0101 234-5678", "+201012345678")]
    [InlineData("٠١٠١٢٣٤٥٦٧٨", "+201012345678")]
    [InlineData("00201012345678", "+201012345678")]
    [InlineData("+966 50 123 4567", "+966501234567")]
    public void Different_spellings_of_one_number_normalize_the_same(string input, string expected) =>
        Assert.Equal(expected, PhoneNumber.Normalize(input));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("hello")]
    [InlineData("12345")]
    [InlineData("+0123456789")]
    [InlineData("+1234567890123456")]
    [InlineData("0101234567")]
    public void Things_that_are_not_phone_numbers_are_refused(string? input) => Assert.Null(PhoneNumber.Normalize(input));
}

public sealed class PhoneProtectorTests
{
    private static PhoneProtector Make(string? enc = null, string? hash = null) => new(Options.Create(new IdentityOptions
    {
        PhoneEncryptionKey = enc ?? Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)),
        PhoneHashKey = hash ?? Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)),
    }));

    [Fact]
    public void Encrypted_phone_round_trips_and_does_not_contain_the_number()
    {
        var p = Make();
        var stored = p.Encrypt("+201012345678");
        Assert.DoesNotContain("1012345678", stored);
        Assert.Equal("+201012345678", p.Decrypt(stored));
        Assert.NotEqual(stored, p.Encrypt("+201012345678")); // fresh nonce each time
    }

    [Fact]
    public void Hash_is_stable_for_one_key_and_different_for_another()
    {
        var hashKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        Assert.Equal(Make(hash: hashKey).Hash("+20101"), Make(hash: hashKey).Hash("+20101"));
        Assert.NotEqual(Make().Hash("+20101"), Make().Hash("+20101"));
    }

    [Fact]
    public void Missing_or_short_keys_fail_loudly_instead_of_storing_plain_numbers()
    {
        Assert.Throws<InvalidOperationException>(() => new PhoneProtector(Options.Create(new IdentityOptions())).Encrypt("+201012345678"));
        Assert.Throws<InvalidOperationException>(() => Make(enc: Convert.ToBase64String(new byte[16])).Encrypt("+201012345678"));
    }

    [Fact]
    public void Tampered_ciphertext_is_rejected()
    {
        var p = Make();
        var bytes = Convert.FromBase64String(p.Encrypt("+201012345678"));
        bytes[^1] ^= 0xFF;
        Assert.ThrowsAny<CryptographicException>(() => p.Decrypt(Convert.ToBase64String(bytes)));
    }
}

public sealed class RolePolicyTests
{
    private static async Task<bool> Allowed(string policy, params UserRole[] roles)
    {
        var services = new ServiceCollection().AddLogging().AddAuthorization(RatableAuth.AddPolicies).BuildServiceProvider();
        var auth = services.GetRequiredService<IAuthorizationService>();
        var claims = roles.Select(r => new Claim(ClaimTypes.Role, r.ToString())).Append(new Claim(ClaimTypes.NameIdentifier, "u1"));
        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, "test"));
        return (await auth.AuthorizeAsync(principal, null, policy)).Succeeded;
    }

    [Fact]
    public async Task Admin_policy_is_for_admins_only()
    {
        Assert.True(await Allowed(RatableAuth.Policies.Admin, UserRole.User, UserRole.Admin));
        Assert.False(await Allowed(RatableAuth.Policies.Admin, UserRole.User, UserRole.Moderator));
        Assert.False(await Allowed(RatableAuth.Policies.Admin, UserRole.User, UserRole.BusinessOwner));
        Assert.False(await Allowed(RatableAuth.Policies.Admin, UserRole.User));
    }

    [Fact]
    public async Task Moderator_policy_allows_moderators_and_admins_only()
    {
        Assert.True(await Allowed(RatableAuth.Policies.Moderator, UserRole.Moderator));
        Assert.True(await Allowed(RatableAuth.Policies.Moderator, UserRole.Admin));
        Assert.False(await Allowed(RatableAuth.Policies.Moderator, UserRole.User, UserRole.BusinessOwner));
    }

    [Fact]
    public async Task Business_owner_policy_is_not_given_to_admins_automatically()
    {
        Assert.True(await Allowed(RatableAuth.Policies.BusinessOwner, UserRole.BusinessOwner));
        Assert.False(await Allowed(RatableAuth.Policies.BusinessOwner, UserRole.Admin));
    }

    [Fact]
    public async Task Signed_in_policy_needs_an_authenticated_user()
    {
        Assert.True(await Allowed(RatableAuth.Policies.SignedIn, UserRole.User));
        var services = new ServiceCollection().AddLogging().AddAuthorization(RatableAuth.AddPolicies).BuildServiceProvider();
        var anonymous = await services.GetRequiredService<IAuthorizationService>()
            .AuthorizeAsync(new ClaimsPrincipal(new ClaimsIdentity()), null, RatableAuth.Policies.SignedIn);
        Assert.False(anonymous.Succeeded);
    }

    [Fact]
    public void A_new_account_is_a_plain_user_and_its_phone_is_not_verified()
    {
        var user = new User();
        Assert.Equal([UserRole.User], user.Roles);
        Assert.False(user.PhoneVerified);
    }
}
