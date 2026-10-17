using System.Net;
using Microsoft.AspNetCore.Mvc.Testing;
using Testcontainers.MongoDb;

namespace Ratable.Api.Tests;

public sealed class HealthTests : IAsyncLifetime
{
    private readonly MongoDbContainer _mongo = new MongoDbBuilder("mongo:8.0").Build();

    public Task InitializeAsync() => _mongo.StartAsync();
    public Task DisposeAsync() => _mongo.DisposeAsync().AsTask();

    private WebApplicationFactory<Program> Factory(string connectionString) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("Mongo:ConnectionString", connectionString);
            b.UseSetting("Mongo:Database", "ratable_tests");
        });

    [Fact]
    public async Task Health_answers_even_when_mongo_is_down()
    {
        await using var factory = Factory("mongodb://127.0.0.1:1/?serverSelectionTimeoutMS=500");
        var response = await factory.CreateClient().GetAsync("/health");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Ready_is_healthy_with_mongo()
    {
        await using var factory = Factory(_mongo.GetConnectionString());
        var response = await factory.CreateClient().GetAsync("/health/ready");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Ready_is_unhealthy_without_mongo()
    {
        await using var factory = Factory("mongodb://127.0.0.1:1/?serverSelectionTimeoutMS=500");
        var response = await factory.CreateClient().GetAsync("/health/ready");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
    }

    [Fact]
    public async Task OpenApi_document_is_published()
    {
        await using var factory = Factory(_mongo.GetConnectionString());
        var response = await factory.CreateClient().GetAsync("/openapi/v1.json");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("\"openapi\"", await response.Content.ReadAsStringAsync());
    }
}
