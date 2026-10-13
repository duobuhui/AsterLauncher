namespace AsterLauncher.Core;

/// <summary>Named local archives when the publisher's history does not expose a reliable account ID.</summary>
public sealed record EndfieldGachaAccount(string Id, string Name, bool IsLegacy);

public interface IEndfieldGachaAccountService : IEndfieldGachaArchiveService
{
    string SelectedAccountId { get; }
    Task<IReadOnlyList<EndfieldGachaAccount>> GetAccountsAsync(CancellationToken cancellationToken = default);
    Task SelectAccountAsync(string accountId, CancellationToken cancellationToken = default);
    Task<EndfieldGachaAccount> CreateAccountAsync(string name, CancellationToken cancellationToken = default);
}
