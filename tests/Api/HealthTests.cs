using System.Net;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Ratable.Api.Tests;

/// <summary>
/// Tests that need a real MongoDB read its connection string from RATABLE_TEST_MONGO
/// (for example mongodb://localhost:27017). No containers are used. They are skipped when it is not set.
/// </summary>
public sealed class HealthTests
{
    private const string Unreachable = "mongodb://127.0.0.1:1/?serverSelectionTimeoutMS=500";
    private static readonly string? RealMongo = Environment.GetEnvironmentVariable("RATABLE_TEST_MONGO");

    private static WebApplicationFactory<Program> Factory(string connectionString) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("Mongo:ConnectionString", connectionString);
            b.UseSetting("Mongo:Database", $"ratable_tests_{Guid.NewGuid():N}");
        });

    [Fact]
    public async Task Health_answers_even_when_mongo_is_down()
    {
        await using var factory = Factory(Unreachable);
        var response = await factory.CreateClient().GetAsync("/health");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Ready_is_unhealthy_without_mongo()
    {
        await using var factory = Factory(Unreachable);
        var response = await factory.CreateClient().GetAsync("/health/ready");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
    }

    [Fact]
    public async Task OpenApi_document_is_published()
    {
        await using var factory = Factory(Unreachable);
        var response = await factory.CreateClient().GetAsync("/openapi/v1.json");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("\"openapi\"", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Ready_is_healthy_with_mongo()
    {
        if (RealMongo is null) return; // needs a local MongoDB; see class summary
        await using var factory = Factory(RealMongo);
        var response = await factory.CreateClient().GetAsync("/health/ready");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}
