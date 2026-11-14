namespace DataBridge.ArchitectureTests;

/// <summary>
/// The allowed project dependencies from <c>docs/architecture.md</c> §4 and the packages
/// that must stay out of the inner layers (CLAUDE.md rule 14).
/// </summary>
internal static class DependencyRules
{
    public const string Shared = "DataBridge.Shared";
    public const string Core = "DataBridge.Core";
    public const string Application = "DataBridge.Application";
    public const string Infrastructure = "DataBridge.Infrastructure";
    public const string Desktop = "DataBridge.Desktop";

    /// <summary>Every source project and the source projects it may reference directly.</summary>
    public static readonly IReadOnlyDictionary<string, string[]> AllowedProjectReferences =
        new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            [Shared] = [],
            [Core] = [Shared],
            [Application] = [Core, Shared],
            [Infrastructure] = [Application, Core, Shared],
            [Desktop] = [Application, Infrastructure],
        };

    /// <summary>
    /// Every test and benchmark project and the source projects it may reference: the project
    /// under test only, except the architecture tests, which inspect the inner layers.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string[]> AllowedTestProjectReferences =
        new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            ["DataBridge.Core.Tests"] = [Core],
            ["DataBridge.Application.Tests"] = [Application],
            ["DataBridge.Infrastructure.Tests"] = [Infrastructure],
            ["DataBridge.Desktop.Tests"] = [Desktop],
            ["DataBridge.IntegrationTests"] = [Infrastructure],
            ["DataBridge.PerformanceTests"] = [Infrastructure],
            ["DataBridge.ArchitectureTests"] = [Shared, Core, Application, Infrastructure],
            ["DataBridge.Benchmarks"] = [Core, Application, Infrastructure],
        };

    /// <summary>Projects that must not depend on UI, database driver, DacFx, SQLite or logging-sink packages.</summary>
    public static readonly string[] InnerLayers = [Shared, Core, Application];

    /// <summary>Package and assembly name prefixes forbidden in <see cref="InnerLayers"/>.</summary>
    public static readonly string[] InfrastructurePackagePrefixes =
    [
        // UI
        "Avalonia",
        "CommunityToolkit.Mvvm",
        "SkiaSharp",
        // Database drivers and data access
        "Dapper",
        "Microsoft.Data.SqlClient",
        "Microsoft.Data.Sqlite",
        "Microsoft.EntityFrameworkCore",
        "Microsoft.SqlServer", // includes DacFx (Microsoft.SqlServer.DacFx / Microsoft.SqlServer.Dac)
        "MySql.Data",
        "MySqlConnector",
        "Npgsql",
        "Oracle.ManagedDataAccess",
        "sqlite-net-pcl",
        "SQLitePCLRaw",
        "System.Data.Odbc",
        "System.Data.OleDb",
        "System.Data.SqlClient",
        "System.Data.SQLite",
        // Authentication used by the SQL Server provider
        "Azure.Identity",
        "Microsoft.Identity.Client",
        // Logging sinks (Microsoft.Extensions.Logging.Abstractions stays allowed)
        "Serilog",
        // Test infrastructure
        "Testcontainers",
        "DotNet.Testcontainers",
    ];

    /// <summary>
    /// Packages forbidden in <see cref="InnerLayers"/> by exact name, because a prefix rule would also block
    /// an allowed sibling: the logging implementation, but not <c>Microsoft.Extensions.Logging.Abstractions</c>.
    /// </summary>
    public static readonly string[] InfrastructurePackageNames =
    [
        "Microsoft.Extensions.Logging",
        "Microsoft.Extensions.Logging.Console",
        "Microsoft.Extensions.Logging.Debug",
        "Microsoft.Extensions.Hosting",
    ];

    public static bool IsInfrastructurePackage(string name) =>
        InfrastructurePackageNames.Contains(name, StringComparer.OrdinalIgnoreCase)
        || InfrastructurePackagePrefixes.Any(prefix =>
            name.Equals(prefix, StringComparison.OrdinalIgnoreCase)
            || name.StartsWith(prefix + ".", StringComparison.OrdinalIgnoreCase));
}
