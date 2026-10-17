using MongoDB.Driver;
using Ratable.Modules.Identity.Models;
using Ratable.Shared.Data;

namespace Ratable.Modules.Identity.Data;

public sealed class UserRepository(IMongoDatabase database) : MongoRepository<User>(database, Collections.Users)
{
    /// <summary>Throws a MongoWriteException (DuplicateKey) when the phone hash or the social login is already used.</summary>
    public Task InsertAsync(User user, CancellationToken ct = default) => Collection.InsertOneAsync(user, cancellationToken: ct);

    public async Task<User?> GetAsync(string id, CancellationToken ct = default) =>
        await Collection.Find(u => u.Id == id).FirstOrDefaultAsync(ct);

    public async Task<User?> GetByPhoneHashAsync(string phoneHash, CancellationToken ct = default) =>
        await Collection.Find(u => u.PhoneHash == phoneHash).FirstOrDefaultAsync(ct);

    public async Task<User?> GetByLoginAsync(string provider, string subject, CancellationToken ct = default) =>
        await Collection.Find(u => u.Logins.Any(l => l.Provider == provider && l.Subject == subject)).FirstOrDefaultAsync(ct);

    public async Task<List<User>> ListByIdsAsync(IReadOnlyCollection<string> ids, CancellationToken ct = default)
    {
        var valid = ids.Where(i => MongoDB.Bson.ObjectId.TryParse(i, out _)).ToList();
        return valid.Count == 0 ? [] : await Collection.Find(u => valid.Contains(u.Id)).ToListAsync(ct);
    }

    /// <summary>For the admin user list: newest first, optionally one role and/or status.</summary>
    public async Task<(List<User> Items, long Total)> SearchAsync(UserRole? role, UserStatus? status, int skip, int limit, CancellationToken ct = default)
    {
        var f = Builders<User>.Filter;
        var filter = f.Empty;
        if (role is not null) filter &= f.AnyEq(u => u.Roles, role.Value);
        if (status is not null) filter &= f.Eq(u => u.Status, status.Value);
        return (await Collection.Find(filter).SortByDescending(u => u.CreatedAt).ThenByDescending(u => u.Id).Skip(skip).Limit(limit).ToListAsync(ct), await Collection.CountDocumentsAsync(filter, cancellationToken: ct));
    }

    public Task ReplaceAsync(User user, CancellationToken ct = default) =>
        Collection.ReplaceOneAsync(u => u.Id == user.Id, user, cancellationToken: ct);
}
