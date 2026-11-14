using DataBridge.Application.Abstractions;
using DataBridge.Application.Connections;
using DataBridge.Desktop.DependencyInjection;
using DataBridge.Desktop.ViewModels;
using DataBridge.Infrastructure.DependencyInjection;
using DataBridge.Infrastructure.Logging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace DataBridge.Desktop.Tests.ViewModels;

/// <summary>
/// The real composition root (use cases, SQLite repository, logging) in a throwaway folder, with an
/// in-memory credential store in place of the OS one.
/// </summary>
internal sealed class DesktopFixture : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "DataBridge.Tests", Guid.NewGuid().ToString("N"));
    private readonly IHost _host;

    public DesktopFixture()
    {
        _host = DesktopServiceCollectionExtensions.BuildHost(
            [],
            new InfrastructureOptions(DatabasePath),
            new LoggingOptions(Path.Combine(_root, "logs")),
            services =>
            {
                services.Replace(ServiceDescriptor.Singleton<ICredentialStore>(CredentialStore));
                services.Replace(ServiceDescriptor.Singleton<IDatabaseProvider>(DatabaseProvider));
                services.Replace(ServiceDescriptor.Singleton<ISchemaReader>(SchemaReader));
            });
    }

    public string DatabasePath => Path.Combine(_root, "databridge.db");

    public string LogDirectory => Path.Combine(_root, "logs");

    public FakeCredentialStore CredentialStore { get; } = new();

    public FakeDatabaseProvider DatabaseProvider { get; } = new();

    public FakeSchemaReader SchemaReader { get; } = new();

    public SchemaComparisonViewModel SchemaComparison => Services.GetRequiredService<SchemaComparisonViewModel>();

    public IServiceProvider Services => _host.Services;

    public MainWindowViewModel MainWindow => Services.GetRequiredService<MainWindowViewModel>();

    public ConnectionsViewModel Connections => Services.GetRequiredService<ConnectionsViewModel>();

    /// <summary>Runs startup and returns the Connections page with its profiles loaded.</summary>
    public async Task<ConnectionsViewModel> StartAsync()
    {
        await MainWindow.InitializeCommand.ExecuteAsync(null);
        Assert.True(MainWindow.IsReady, MainWindow.StartupError);
        return Connections;
    }

    /// <summary>Stops the app (flushing and closing the log) but keeps its files for inspection.</summary>
    public void StopHost() => _host.Dispose();

    public void Dispose()
    {
        _host.Dispose();
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}

/// <summary>An in-memory credential store whose availability and behavior the test controls.</summary>
internal sealed class FakeCredentialStore : ICredentialStore
{
    public const string UnavailableReason = "No secret service is running.";

    public Dictionary<string, Secret> Secrets { get; } = new(StringComparer.Ordinal);

    public bool IsAvailable { get; set; } = true;

    /// <summary>When set, <see cref="SaveAsync"/> waits for it (honoring cancellation) before saving.</summary>
    public TaskCompletionSource? SaveGate { get; set; }

    /// <summary>When set, <see cref="SaveAsync"/> throws it.</summary>
    public Exception? SaveFailure { get; set; }

    public Task<CredentialStoreStatus> GetStatusAsync(CancellationToken cancellationToken) =>
        Task.FromResult(IsAvailable ? CredentialStoreStatus.Available : CredentialStoreStatus.Unavailable(UnavailableReason));

    public async Task SaveAsync(string reference, Secret secret, CancellationToken cancellationToken)
    {
        if (SaveGate is { } gate)
        {
            await gate.Task.WaitAsync(cancellationToken);
        }

        if (SaveFailure is { } failure)
        {
            throw failure;
        }

        lock (Secrets)
        {
            Secrets[reference] = secret;
        }
    }

    public Task<Secret?> FindAsync(string reference, CancellationToken cancellationToken)
    {
        lock (Secrets)
        {
            return Task.FromResult(Secrets.GetValueOrDefault(reference));
        }
    }

    public Task DeleteAsync(string reference, CancellationToken cancellationToken)
    {
        lock (Secrets)
        {
            Secrets.Remove(reference);
        }

        return Task.CompletedTask;
    }
}

/// <summary>A database provider that returns a prepared result instead of connecting.</summary>
internal sealed class FakeDatabaseProvider : IDatabaseProvider
{
    public List<DatabaseConnectionRequest> Requests { get; } = [];

    public DataBridge.Shared.Results.Result<DataBridge.Core.Connections.ResolvedDatabaseIdentity> Next { get; set; } =
        DataBridge.Shared.Results.Result.Success(DataBridge.Core.Connections.ResolvedDatabaseIdentity.Create(
            DataBridge.Core.Connections.DatabaseProviderType.SqlServer,
            "DB01",
            "Sales",
            5,
            "SQL Server 2022 (16.0.4135.4) Developer Edition (64-bit)",
            "Latin1_General_CI_AS"));

    /// <summary>When set, decides the result per request instead of <see cref="Next"/>.</summary>
    public Func<DatabaseConnectionRequest, DataBridge.Shared.Results.Result<DataBridge.Core.Connections.ResolvedDatabaseIdentity>>? Resolve { get; set; }

    public DataBridge.Core.Connections.DatabaseProviderType ProviderType => DataBridge.Core.Connections.DatabaseProviderType.SqlServer;

    public Task<DataBridge.Shared.Results.Result<DataBridge.Core.Connections.ResolvedDatabaseIdentity>> ResolveIdentityAsync(
        DatabaseConnectionRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (Requests)
        {
            Requests.Add(request);
        }

        return Task.FromResult(Resolve?.Invoke(request) ?? Next);
    }
}

/// <summary>A schema reader that returns a prepared schema per database name instead of connecting.</summary>
internal sealed class FakeSchemaReader : ISchemaReader
{
    public Dictionary<string, DataBridge.Core.Schema.DatabaseSchema> Schemas { get; } = new(StringComparer.Ordinal);

    /// <summary>When set, reads wait for it (honoring cancellation).</summary>
    public TaskCompletionSource? ReadGate { get; set; }

    public DataBridge.Core.Connections.DatabaseProviderType ProviderType => DataBridge.Core.Connections.DatabaseProviderType.SqlServer;

    public DataBridge.Core.Schema.ISqlDefinitionNormalizer DefinitionNormalizer => DataBridge.Core.Schema.ExactSqlDefinitionNormalizer.Instance;

    public async Task<DataBridge.Shared.Results.Result<DataBridge.Core.Schema.DatabaseSchema>> ReadSchemaAsync(
        DatabaseConnectionRequest request,
        CancellationToken cancellationToken)
    {
        if (ReadGate is { } gate)
        {
            await gate.Task.WaitAsync(cancellationToken);
        }

        var schema = Schemas.GetValueOrDefault(request.Profile.DatabaseName)
            ?? new DataBridge.Core.Schema.DatabaseSchema(request.Profile.DatabaseName, [], [], false, true, DateTimeOffset.UnixEpoch);
        return DataBridge.Shared.Results.Result.Success(schema);
    }
}
