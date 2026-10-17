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

    public Task ReplaceAsync(User user, CancellationToken ct = default) =>
        Collection.ReplaceOneAsync(u => u.Id == user.Id, user, cancellationToken: ct);
}
