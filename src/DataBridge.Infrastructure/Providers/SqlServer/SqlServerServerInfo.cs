using System.Globalization;
using DataBridge.Application.Connections;
using DataBridge.Shared.Errors;

namespace DataBridge.Infrastructure.Providers.SqlServer;

/// <summary>What the identity query returns, and the support policy for it (ADR-0010).</summary>
internal sealed record SqlServerServerInfo(
    string ServerName,
    string DatabaseName,
    int DatabaseId,
    int MajorVersion,
    string ProductVersion,
    string Edition,
    int EngineEdition,
    string? DatabaseCollation,
    Guid? DatabaseGuid = null,
    DateTime? DatabaseCreateDate = null)
{
    /// <summary>SQL Server 2017.</summary>
    public const int MinimumMajorVersion = 14;

    /// <summary>
    /// Reads identity and version in one round trip. <c>SERVERPROPERTY('ServerName')</c> is used rather than
    /// <c>@@SERVERNAME</c>, which can be stale after a rename. The query only reads.
    /// </summary>
    public const string Query =
        """
        SELECT
            CAST(SERVERPROPERTY('ServerName') AS nvarchar(256)),
            DB_NAME(),
            DB_ID(),
            CAST(SERVERPROPERTY('ProductMajorVersion') AS int),
            CAST(SERVERPROPERTY('ProductVersion') AS nvarchar(128)),
            CAST(SERVERPROPERTY('Edition') AS nvarchar(128)),
            CAST(SERVERPROPERTY('EngineEdition') AS int),
            CAST(DATABASEPROPERTYEX(DB_NAME(), 'Collation') AS nvarchar(128)),
            (SELECT service_broker_guid FROM sys.databases WHERE database_id = DB_ID()),
            (SELECT create_date FROM sys.databases WHERE database_id = DB_ID());
        """;

    /// <summary>
    /// The major version: <c>SERVERPROPERTY('ProductMajorVersion')</c>, which is NULL before SQL Server 2012 SP3,
    /// otherwise the first part of <c>ProductVersion</c>; 0 when neither can be read (treated as unsupported).
    /// </summary>
    public static int ParseMajorVersion(int? productMajorVersion, string? productVersion)
    {
        if (productMajorVersion is { } major)
        {
            return major;
        }

        var first = productVersion?.Split('.')[0];
        return int.TryParse(first, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) ? parsed : 0;
    }

    /// <summary>Product name for the version, for example "SQL Server 2022".</summary>
    public string ProductName =>
        MajorVersion switch
        {
            10 => "SQL Server 2008",
            11 => "SQL Server 2012",
            12 => "SQL Server 2014",
            13 => "SQL Server 2016",
            14 => "SQL Server 2017",
            15 => "SQL Server 2019",
            16 => "SQL Server 2022",
            17 => "SQL Server 2025",
            _ => string.Create(CultureInfo.InvariantCulture, $"SQL Server (version {MajorVersion})"),
        };

    /// <summary>For example "SQL Server 2022 (16.0.4135.4) Developer Edition (64-bit)".</summary>
    public string DisplayVersion => $"{ProductName} ({ProductVersion}) {Edition}";

    /// <summary>
    /// <see langword="null"/> when the server is supported; otherwise why not. Boxed SQL Server editions
    /// (engine editions 1 to 4) from 2017 on are supported; Azure SQL, Synapse, Edge and Fabric are not yet.
    /// </summary>
    public Error? CheckSupported()
    {
        var engine = EngineEdition switch
        {
            1 or 2 or 3 or 4 => null,
            5 => "Azure SQL Database",
            6 => "Azure Synapse Analytics",
            8 => "Azure SQL Managed Instance",
            9 => "Azure SQL Edge",
            11 => "Azure Synapse serverless SQL pool",
            12 => "SQL database in Microsoft Fabric",
            _ => string.Create(CultureInfo.InvariantCulture, $"SQL Server engine edition {EngineEdition}"),
        };

        if (engine is not null)
        {
            return ConnectionErrors.UnsupportedEngine(engine);
        }

        return MajorVersion < MinimumMajorVersion
            ? ConnectionErrors.UnsupportedServerVersion($"{ProductName} ({ProductVersion})")
            : null;
    }
}
