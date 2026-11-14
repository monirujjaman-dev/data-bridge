using DataBridge.Core.Connections;

namespace DataBridge.Core.Tests.Connections;

public sealed class ResolvedDatabaseIdentityTests
{
    [Fact]
    public void Fingerprint_is_stable_for_the_same_fields()
    {
        var first = Identity("DB01", "Sales", 5);
        var second = Identity("DB01", "Sales", 5, version: "SQL Server 2022 (16.0.1) Developer Edition", collation: "Latin1_General_CS_AS");

        Assert.Equal(first.IdentityFingerprint, second.IdentityFingerprint);
        Assert.True(first.IsSameDatabaseAs(second));
        Assert.Matches("^[0-9a-f]{64}$", first.IdentityFingerprint);
    }

    [Fact]
    public void Server_name_case_does_not_change_the_fingerprint() =>
        Assert.True(Identity("db01", "Sales", 5).IsSameDatabaseAs(Identity("DB01", "Sales", 5)));

    [Theory]
    [InlineData("DB02", "Sales", 5)]
    [InlineData("DB01", "Sales2", 5)]
    [InlineData("DB01", "Sales", 6)]
    [InlineData("DB01", "Sales", null)]
    public void Any_identifying_field_changes_the_fingerprint(string server, string database, int? databaseId) =>
        Assert.False(Identity("DB01", "Sales", 5).IsSameDatabaseAs(Identity(server, database, databaseId)));

    [Fact]
    public void Database_guid_and_create_date_tell_apart_databases_that_share_name_and_id()
    {
        var created = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Unspecified);
        var original = Identity("DB01", "Sales", 5, guid: Guid.Parse("11111111-1111-1111-1111-111111111111"), created: created);

        Assert.False(original.IsSameDatabaseAs(Identity("DB01", "Sales", 5, guid: Guid.Parse("22222222-2222-2222-2222-222222222222"), created: created)));
        Assert.False(original.IsSameDatabaseAs(Identity("DB01", "Sales", 5, guid: original.DatabaseGuid, created: created.AddSeconds(1))));
        Assert.True(original.IsSameDatabaseAs(Identity("db01", "Sales", 5, guid: original.DatabaseGuid, created: created)));
    }

    [Fact]
    public void Missing_values_differ_from_empty_ones() =>
        Assert.False(Identity("DB01", "Sales", null).IsSameDatabaseAs(Identity("DB01", "Sales", 0)));

    [Fact]
    public void Field_boundaries_are_unambiguous() =>
        Assert.False(Identity("DB01", "1;Sales", 5).IsSameDatabaseAs(Identity("DB01;1", "Sales", 5)));

    [Theory]
    [InlineData("", "Sales", "v")]
    [InlineData("DB01", " ", "v")]
    [InlineData("DB01", "Sales", "")]
    public void Required_fields_are_checked(string server, string database, string version) =>
        Assert.ThrowsAny<ArgumentException>(() =>
            ResolvedDatabaseIdentity.Create(DatabaseProviderType.SqlServer, server, database, 1, version, null));

    private static ResolvedDatabaseIdentity Identity(
        string server,
        string database,
        int? databaseId,
        string version = "SQL Server 2019 (15.0.1) Express Edition",
        string? collation = "SQL_Latin1_General_CP1_CI_AS",
        Guid? guid = null,
        DateTime? created = null) =>
        ResolvedDatabaseIdentity.Create(DatabaseProviderType.SqlServer, server, database, databaseId, version, collation, guid, created);
}
