namespace Ratable.Shared.Storage;

/// <summary>
/// Private, encrypted file storage that is never served to the public. Proof files and claim documents live here (CLAUDE.md rule 5).
/// Keys are random 32-character hex names made by the caller.
/// </summary>
public interface IPrivateFileStore
{
    Task SaveAsync(string key, byte[] content, CancellationToken cancellationToken = default);
    Task<byte[]?> ReadAsync(string key, CancellationToken cancellationToken = default);
    Task DeleteAsync(string key, CancellationToken cancellationToken = default);
    Task<bool> ExistsAsync(string key, CancellationToken cancellationToken = default);
}
