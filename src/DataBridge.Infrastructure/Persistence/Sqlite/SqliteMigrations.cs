namespace DataBridge.Infrastructure.Persistence.Sqlite;

/// <summary>One versioned schema change. Applied migrations must never be edited; add a new one instead.</summary>
/// <param name="Version">Sequential version, starting at 1; stored in <c>PRAGMA user_version</c>.</param>
/// <param name="Description">What the migration does.</param>
/// <param name="Sql">The statements to run.</param>
internal sealed record SqliteMigration(int Version, string Description, string Sql);

/// <summary>The ordered list of local database migrations.</summary>
internal static class SqliteMigrations
{
    public static IReadOnlyList<SqliteMigration> All { get; } =
    [
        new(
            1,
            "Create connection_profiles",
            """
            CREATE TABLE connection_profiles (
                id                       TEXT    NOT NULL PRIMARY KEY,
                name                     TEXT    NOT NULL,
                provider                 TEXT    NOT NULL,
                server                   TEXT    NOT NULL,
                database_name            TEXT    NOT NULL,
                authentication_type      TEXT    NOT NULL,
                user_name                TEXT    NULL,
                credential_reference     TEXT    NULL,
                encrypt                  INTEGER NOT NULL CHECK (encrypt IN (0, 1)),
                trust_server_certificate INTEGER NOT NULL CHECK (trust_server_certificate IN (0, 1)),
                connect_timeout_seconds  INTEGER NOT NULL,
                application_name         TEXT    NULL,
                created_at_utc           TEXT    NOT NULL,
                updated_at_utc           TEXT    NOT NULL
            ) STRICT;

            CREATE UNIQUE INDEX ux_connection_profiles_name ON connection_profiles (name COLLATE NOCASE);
            """),
        new(
            2,
            "Add the environment label to connection_profiles",
            """
            ALTER TABLE connection_profiles ADD COLUMN environment TEXT NOT NULL DEFAULT 'None';
            """),
    ];

    public static int LatestVersion => All[^1].Version;
}
