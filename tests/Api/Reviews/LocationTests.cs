using System.Net;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Bson;
using MongoDB.Driver;
using MongoDB.Driver.GeoJsonObjectModel;
using Ratable.Api.Tests.Data;
using Ratable.Api.Tests.Identity;
using Ratable.Modules.Entities.Data;
using Ratable.Modules.Entities.Models;
using Ratable.Modules.Reviews.Data;
using Ratable.Modules.Reviews.Models;
using static Ratable.Api.Tests.TestKit;

namespace Ratable.Api.Tests.Reviews;

public sealed class LocationTests
{
    private const double Lat = 30.0444;
    private const double Lng = 31.2357; // central Cairo

    // 0.009 degrees of latitude is about one kilometre.
    private static async Task<Entity> Place(IdentityApiFactory api, string name, double kmNorth, string category = "compounds")
    {
        var entity = new Entity
        {
            CategoryId = category,
            Name = new Ratable.Shared.Data.LocalizedText("مكان", name),
            City = "Cairo",
            Status = EntityStatus.Active,
            Location = GeoJson.Point(GeoJson.Geographic(Lng, Lat + kmNorth * 0.009)),
        };
        entity.Slugs = new Dictionary<string, string> { ["en"] = name.ToLowerInvariant().Replace(' ', '-'), ["ar"] = "مكان" };
        await api.Services.GetRequiredService<EntityRepository>().InsertAsync(entity);
        return entity;
    }

    private static async Task Rate(IdentityApiFactory api, Entity e, int score, int count = 1, string criterion = "security")
    {
        for (var i = 0; i < count; i++)
            await api.Services.GetRequiredService<ReviewRepository>().InsertAsync(new Review
            {
                EntityId = e.Id,
                UserId = ObjectId.GenerateNewId().ToString(),
                CategoryId = e.CategoryId,
                Status = ReviewStatus.Published,
                Verification = new ReviewVerification { Level = VerificationLevel.Verified },
                Answers = [new ReviewAnswer { CriterionId = criterion, Score = score }],
            });
    }

    private static List<string> Names(System.Text.Json.JsonElement json) => [.. json.GetProperty("places").EnumerateArray().Select(p => p.GetProperty("name").GetProperty("en").GetString()!)];

    private static string Near(double lat = Lat, double lng = Lng, int radiusKm = 5, string extra = "") => $"/api/places?lat={lat}&lng={lng}&radiusKm={radiusKm}{extra}";

    // ---- the seeded areas ----

    [MongoFact]
    public async Task The_seeded_areas_can_be_listed_searched_and_filtered_in_both_languages()
    {
        await using var api = new IdentityApiFactory();
        var all = (await Send(api, HttpMethod.Get, "/api/areas")).Json.EnumerateArray().ToList();
        Assert.True(all.Count >= 25);
        var ids = all.Select(a => a.GetProperty("id").GetString()).ToList();
        foreach (var id in new[] { "cairo", "giza", "alexandria", "riyadh", "jeddah", "dubai", "abu-dhabi" }) Assert.Contains(id, ids);

        var arabic = (await Send(api, HttpMethod.Get, $"/api/areas?q={Uri.EscapeDataString("الشيخ زايد")}")).Json.EnumerateArray().Select(a => a.GetProperty("id").GetString());
        Assert.Contains("sheikh-zayed", arabic);
        // Spelling variants the normalizer folds together (a different alef, a final ya) find the same place.
        var variant = (await Send(api, HttpMethod.Get, $"/api/areas?q={Uri.EscapeDataString("الاسكندريه")}")).Json.EnumerateArray().Select(a => a.GetProperty("id").GetString());
        Assert.Contains("alexandria", variant);
        var english = (await Send(api, HttpMethod.Get, "/api/areas?q=cairo")).Json.EnumerateArray().Select(a => a.GetProperty("id").GetString()).ToList();
        Assert.Contains("cairo", english);
        Assert.Contains("new-cairo", english);

        var saudi = (await Send(api, HttpMethod.Get, "/api/areas?country=sa")).Json.EnumerateArray().ToList();
        Assert.NotEmpty(saudi);
        Assert.All(saudi, a => Assert.Equal("SA", a.GetProperty("country").GetString()));

        Assert.Equal(HttpStatusCode.NotFound, (await Send(api, HttpMethod.Get, "/api/areas/atlantis")).Response.StatusCode);
        Assert.Equal("Cairo", (await Send(api, HttpMethod.Get, "/api/areas/cairo")).Json.GetProperty("name").GetProperty("en").GetString());
    }

    [MongoFact]
    public async Task Geocoding_and_reverse_geocoding_use_the_seeded_list()
    {
        await using var api = new IdentityApiFactory();
        Assert.Equal("dubai", (await Send(api, HttpMethod.Get, "/api/geocode?text=dubai")).Json.GetProperty("id").GetString());
        Assert.Equal(HttpStatusCode.NotFound, (await Send(api, HttpMethod.Get, "/api/geocode?text=nowhere")).Response.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Send(api, HttpMethod.Get, "/api/geocode")).Response.StatusCode);

        Assert.Equal("cairo", (await Send(api, HttpMethod.Get, $"/api/reverse-geocode?lat={Lat}&lng={Lng}")).Json.GetProperty("id").GetString());
        Assert.Equal(HttpStatusCode.NotFound, (await Send(api, HttpMethod.Get, "/api/reverse-geocode?lat=0&lng=0")).Response.StatusCode); // the middle of the ocean
        Assert.Equal(HttpStatusCode.BadRequest, (await Send(api, HttpMethod.Get, "/api/reverse-geocode")).Response.StatusCode);
    }

    // ---- nearby ----

    [MongoFact]
    public async Task Results_stay_inside_the_chosen_distance_and_only_the_five_distances_are_accepted()
    {
        await using var api = new IdentityApiFactory();
        var close = await Place(api, "Close Place", 0.5);
        var middle = await Place(api, "Middle Place", 2);
        var far = await Place(api, "Far Place", 8);
        foreach (var p in new[] { close, middle, far }) await Rate(api, p, 4);

        Assert.Equal(["Close Place"], Names((await Send(api, HttpMethod.Get, Near(radiusKm: 1))).Json));
        Assert.Equal(["Close Place", "Middle Place"], Names((await Send(api, HttpMethod.Get, Near(radiusKm: 3))).Json).Order());
        Assert.Equal(["Close Place", "Middle Place"], Names((await Send(api, HttpMethod.Get, Near(radiusKm: 5))).Json).Order());
        Assert.Equal(3, Names((await Send(api, HttpMethod.Get, Near(radiusKm: 10))).Json).Count);
        Assert.Equal(3, Names((await Send(api, HttpMethod.Get, Near(radiusKm: 25))).Json).Count);

        foreach (var bad in new[] { 0, 2, 7, 50 })
            Assert.Equal(HttpStatusCode.BadRequest, (await Send(api, HttpMethod.Get, Near(radiusKm: bad))).Response.StatusCode);
        // Every returned distance is within the radius.
        var json = (await Send(api, HttpMethod.Get, Near(radiusKm: 3))).Json;
        Assert.All(json.GetProperty("places").EnumerateArray(), p => Assert.InRange(p.GetProperty("distanceMeters").GetDouble(), 0, 3000));
    }

    [MongoFact]
    public async Task Places_are_ranked_by_verified_score_first_and_then_by_distance()
    {
        await using var api = new IdentityApiFactory();
        var nearLow = await Place(api, "Near Low", 0.3);
        var midHigh = await Place(api, "Mid High", 2);
        var nearHigh = await Place(api, "Near High", 0.6);
        var farHigh = await Place(api, "Far High", 4);
        await Rate(api, nearLow, 3);
        await Rate(api, midHigh, 5);
        await Rate(api, nearHigh, 5);
        await Rate(api, farHigh, 5);

        var names = Names((await Send(api, HttpMethod.Get, Near(radiusKm: 5))).Json);

        Assert.Equal(["Near High", "Mid High", "Far High", "Near Low"], names); // score first, nearest first within a score; the near low-scoring place is last
    }

    [MongoFact]
    public async Task Unrated_places_are_left_out_by_default_and_follow_the_rated_ones_when_asked_for()
    {
        await using var api = new IdentityApiFactory();
        var rated = await Place(api, "Rated Far", 3);
        var unrated = await Place(api, "Unrated Near", 0.2);
        var unverified = await Place(api, "Unverified Near", 0.4);
        await Rate(api, rated, 4);
        await api.Services.GetRequiredService<ReviewRepository>().InsertAsync(new Review
        {
            EntityId = unverified.Id,
            UserId = ObjectId.GenerateNewId().ToString(),
            CategoryId = "compounds",
            Status = ReviewStatus.Published,
            Verification = new ReviewVerification { Level = VerificationLevel.Unverified },
            Answers = [new ReviewAnswer { CriterionId = "security", Score = 5 }],
        });

        Assert.Equal(["Rated Far"], Names((await Send(api, HttpMethod.Get, Near())).Json));
        // Unverified reviews give no verified score, so that place is unrated too: nearest first among the unrated.
        Assert.Equal(["Rated Far", "Unrated Near", "Unverified Near"], Names((await Send(api, HttpMethod.Get, Near(extra: "&ratedOnly=false"))).Json));
    }

    [MongoFact]
    public async Task A_category_or_an_area_can_stand_in_for_the_device_location()
    {
        await using var api = new IdentityApiFactory();
        var compound = await Place(api, "Compound Here", 1);
        var restaurant = await Place(api, "Restaurant Here", 1.5, "restaurants-cafes");
        await Rate(api, compound, 4);
        await Rate(api, restaurant, 4, criterion: "taste");

        Assert.Equal(["Restaurant Here"], Names((await Send(api, HttpMethod.Get, Near(extra: "&category=restaurants-cafes"))).Json));
        // From the chosen area alone (no device location): the middle of Cairo.
        var byArea = (await Send(api, HttpMethod.Get, "/api/places?area=cairo&radiusKm=5")).Json;
        Assert.Equal(2, Names(byArea).Count);
        Assert.Equal("cairo", byArea.GetProperty("area").GetProperty("id").GetString());

        Assert.Equal("location_required", (await Send(api, HttpMethod.Get, "/api/places?radiusKm=5")).Json.GetProperty("error").GetString());
        Assert.Equal(HttpStatusCode.NotFound, (await Send(api, HttpMethod.Get, "/api/places?area=atlantis")).Response.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Send(api, HttpMethod.Get, Near(extra: "&category=nope"))).Response.StatusCode);
    }

    [MongoFact]
    public async Task The_device_location_is_used_for_the_answer_only_and_is_never_stored_or_logged()
    {
        await using var api = new IdentityApiFactory();
        var user = await api.SignInAsync("+201111111111");
        await Rate(api, await Place(api, "Somewhere", 0.5), 4);

        // A signed-in person asks, with the location in the header the web app uses, and also through the query.
        const string secretLat = "30.07361234";
        const string secretLng = "31.27895678";
        using var http = api.CreateClient();
        using var request = IdentityApiFactory.WithCookie(HttpMethod.Get, "/api/places?radiusKm=5", user.Cookie!, null);
        request.Headers.Add("X-Visitor-Location", $"{secretLat},{secretLng}");
        var viaHeader = await http.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, viaHeader.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Send(api, HttpMethod.Get, $"/api/places?lat={secretLat}&lng={secretLng}&radiusKm=5", user.Cookie)).Response.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Send(api, HttpMethod.Get, $"/api/reverse-geocode?lat={secretLat}&lng={secretLng}", user.Cookie)).Response.StatusCode);

        // Not echoed back ...
        Assert.DoesNotContain(secretLat[..7], await viaHeader.Content.ReadAsStringAsync());
        // ... not in the logs ...
        Assert.DoesNotContain(api.Logs.Lines, l => l.Contains(secretLat[..7]) || l.Contains(secretLng[..7]));
        // ... and not in any collection of the database: the account, the sessions, the audit log, anything.
        var db = api.Database;
        foreach (var name in await (await db.ListCollectionNamesAsync()).ToListAsync())
        {
            var docs = await db.GetCollection<BsonDocument>(name).Find(FilterDefinition<BsonDocument>.Empty).ToListAsync();
            Assert.DoesNotContain(docs, d => d.ToJson().Contains(secretLat[..7]) || d.ToJson().Contains(secretLng[..7]));
        }
        var users = await db.GetCollection<BsonDocument>("users").Find(FilterDefinition<BsonDocument>.Empty).ToListAsync();
        Assert.All(users, u => Assert.False(u.Contains("location") || u.Contains("lat") || u.Contains("lastKnownLocation")));
    }

    [MongoFact]
    public async Task Ranking_is_the_same_with_and_without_a_visitor_location_for_the_place_list()
    {
        await using var api = new IdentityApiFactory();
        var a = await Place(api, "Alpha", 1);
        var b = await Place(api, "Beta", 6);
        await Rate(api, a, 3, 5);
        await Rate(api, b, 5, 5);

        var plain = Names((await Send(api, HttpMethod.Get, "/api/categories/compounds/places")).Json);
        var located = Names((await Send(api, HttpMethod.Get, $"/api/categories/compounds/places?lat={Lat}&lng={Lng}")).Json);

        Assert.Equal(["Beta", "Alpha"], plain);
        Assert.Equal(plain, located);
    }

    // ---- onboarding choices in the profile ----

    [MongoFact]
    public async Task The_profile_keeps_interests_and_an_area_and_refuses_anything_else()
    {
        await using var api = new IdentityApiFactory();
        var user = await api.SignInAsync("+201111111111");

        var saved = await Send(api, HttpMethod.Put, "/api/me/profile", user.Cookie, new { displayName = "Nour", interests = new[] { "Compounds", "restaurants-cafes", "hotels-resorts" }, areaId = "New-Cairo" });
        Assert.Equal(HttpStatusCode.OK, saved.Response.StatusCode);
        var profile = saved.Json.GetProperty("profile");
        Assert.Equal(["compounds", "restaurants-cafes", "hotels-resorts"], profile.GetProperty("interests").EnumerateArray().Select(i => i.GetString()));
        Assert.Equal("new-cairo", profile.GetProperty("areaId").GetString());

        // Saving only the display name later keeps the choices.
        var renamed = await Send(api, HttpMethod.Put, "/api/me/profile", user.Cookie, new { displayName = "Nour R" });
        Assert.Equal(3, renamed.Json.GetProperty("profile").GetProperty("interests").GetArrayLength());

        Assert.Equal("invalid_interests", (await Send(api, HttpMethod.Put, "/api/me/profile", user.Cookie, new { interests = new[] { "bad value!" } })).Json.GetProperty("error").GetString());
        Assert.Equal("invalid_area", (await Send(api, HttpMethod.Put, "/api/me/profile", user.Cookie, new { areaId = "../../x" })).Json.GetProperty("error").GetString());
    }
}
