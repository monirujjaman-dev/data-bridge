using DataBridge.Application.Abstractions;
using DataBridge.Application.Connections;
using DataBridge.Core.Connections;

namespace DataBridge.Application.Tests.Connections;

internal sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = now;

    public override DateTimeOffset GetUtcNow() => Now;
}

/// <summary>An in-memory repository that honors cancellation like a real one.</summary>
internal sealed class InMemoryConnectionProfileRepository : IConnectionProfileRepository
{
    private readonly Dictionary<Guid, DatabaseConnection> _profiles = [];

    public Exception? FailNextWrite { get; set; }

    /// <summary>Runs inside <see cref="UpdateAsync"/> before the write, to simulate a concurrent change.</summary>
    public Action? BeforeUpdate { get; set; }

    /// <summary>Makes <see cref="NameExistsAsync"/> miss duplicates, as when another window saves at the same time.</summary>
    public bool NameCheckMissesDuplicates { get; set; }

    /// <summary>The last profile passed to <see cref="AddAsync"/>, even if the write failed.</summary>
    public DatabaseConnection? LastAddAttempt { get; private set; }

    public IReadOnlyCollection<DatabaseConnection> Profiles => _profiles.Values;

    public Task<IReadOnlyList<DatabaseConnection>> ListAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        IReadOnlyList<DatabaseConnection> list =
            [.. _profiles.Values.OrderBy(profile => profile.Name, StringComparer.OrdinalIgnoreCase)];
        return Task.FromResult(list);
    }

    public Task<DatabaseConnection?> FindAsync(Guid id, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(_profiles.GetValueOrDefault(id));
    }

    public Task<bool> NameExistsAsync(string name, Guid? excludingId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (NameCheckMissesDuplicates)
        {
            return Task.FromResult(false);
        }

        return Task.FromResult(_profiles.Values.Any(profile =>
            profile.Id != excludingId && string.Equals(profile.Name, name, StringComparison.OrdinalIgnoreCase)));
    }

    public Task AddAsync(DatabaseConnection profile, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        LastAddAttempt = profile;
        ThrowIfFailing();
        ThrowIfNameTaken(profile);
        _profiles.Add(profile.Id, profile);
        return Task.CompletedTask;
    }

    public Task<bool> UpdateAsync(
        DatabaseConnection profile,
        DateTimeOffset expectedUpdatedAtUtc,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfFailing();
        BeforeUpdate?.Invoke();
        if (!_profiles.TryGetValue(profile.Id, out var stored) || stored.UpdatedAtUtc != expectedUpdatedAtUtc)
        {
            return Task.FromResult(false);
        }

        ThrowIfNameTaken(profile);
        _profiles[profile.Id] = profile;
        return Task.FromResult(true);
    }

    public Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfFailing();
        return Task.FromResult(_profiles.Remove(id));
    }

    /// <summary>Stores a profile directly, bypassing the use cases.</summary>
    public void Seed(DatabaseConnection profile) => _profiles[profile.Id] = profile;

    /// <summary>Removes a profile directly, simulating a concurrent delete.</summary>
    public void RemoveBehindTheScenes(Guid id) => _profiles.Remove(id);

    private void ThrowIfNameTaken(DatabaseConnection profile)
    {
        if (_profiles.Values.Any(other =>
            other.Id != profile.Id && string.Equals(other.Name, profile.Name, StringComparison.OrdinalIgnoreCase)))
        {
            throw new DuplicateConnectionProfileNameException();
        }
    }

    private void ThrowIfFailing()
    {
        if (FailNextWrite is { } failure)
        {
            FailNextWrite = null;
            throw failure;
        }
    }
}

internal sealed class FakeCredentialStore : ICredentialStore
{
    public const string UnavailableReason = "No secret service is running.";

    public Dictionary<string, Secret> Secrets { get; } = new(StringComparer.Ordinal);

    public bool IsAvailable { get; set; } = true;

    public Task<CredentialStoreStatus> GetStatusAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(IsAvailable ? CredentialStoreStatus.Available : CredentialStoreStatus.Unavailable(UnavailableReason));
    }

    public Task SaveAsync(string reference, Secret secret, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnsureAvailable();
        Secrets[reference] = secret;
        return Task.CompletedTask;
    }

    public Task<Secret?> FindAsync(string reference, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnsureAvailable();
        return Task.FromResult(Secrets.GetValueOrDefault(reference));
    }

    public Task DeleteAsync(string reference, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnsureAvailable();
        Secrets.Remove(reference);
        return Task.CompletedTask;
    }

    private void EnsureAvailable()
    {
        if (!IsAvailable)
        {
            throw new InvalidOperationException("The credential store is unavailable.");
        }
    }
}

/// <summary>Wires the connection-profile use cases to fakes.</summary>
internal sealed class ConnectionProfileFixture
{
    public static readonly DateTimeOffset Start = new(2026, 11, 14, 9, 0, 0, TimeSpan.Zero);

    public ConnectionProfileFixture()
    {
        Secrets = new ProfileSecretStore(CredentialStore, SessionSecrets);
        Create = new CreateConnectionProfile(Repository, Secrets, Clock);
        Update = new UpdateConnectionProfile(Repository, Secrets, Clock);
        Rename = new RenameConnectionProfile(Repository, Clock);
        Delete = new DeleteConnectionProfile(Repository, Secrets);
        List = new ListConnectionProfiles(Repository);
    }

    public InMemoryConnectionProfileRepository Repository { get; } = new();

    public FakeCredentialStore CredentialStore { get; } = new();

    public SessionSecretCache SessionSecrets { get; } = new();

    public FixedTimeProvider Clock { get; } = new(Start);

    public ProfileSecretStore Secrets { get; }

    public CreateConnectionProfile Create { get; }

    public UpdateConnectionProfile Update { get; }

    public RenameConnectionProfile Rename { get; }

    public DeleteConnectionProfile Delete { get; }

    public ListConnectionProfiles List { get; }

    public static ConnectionProfileInput SqlInput(string name = "Staging", string? password = "s3cr3t!") =>
        new(name, "db01", "Sales", AuthenticationType.SqlAuthentication, "app", password is null ? null : new Secret(password), ConnectionSettings.Default);

    public static ConnectionProfileInput IntegratedInput(string name = "Staging") =>
        new(name, "db01", "Sales", AuthenticationType.Integrated, null, null, ConnectionSettings.Default);

    public async Task<DatabaseConnection> CreateSqlProfileAsync(string name = "Staging", string? password = "s3cr3t!")
    {
        var result = await Create.ExecuteAsync(SqlInput(name, password), TestContext.Current.CancellationToken);
        return result.Value.Profile;
    }
}
