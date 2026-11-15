using DataBridge.Core.Comparison;
using DataBridge.Core.Connections;
using DataBridge.Core.Data;
using DataBridge.Core.Schema;
using DataBridge.Core.Synchronization;
using static DataBridge.Core.Tests.Comparison.SchemaFixtures;
using static DataBridge.Core.Tests.Synchronization.SynchronizationFixtures;

namespace DataBridge.Core.Tests.Synchronization;

public sealed class PlanAndApprovalTests
{
    private static readonly Guid _planId = Guid.Parse("8f1c6c1e-1c55-4b7b-9e9a-4a0b3f6f2d11");
    private static readonly Guid _sessionId = Guid.Parse("2d1c6c1e-1c55-4b7b-9e9a-4a0b3f6f2d12");
    private static readonly Guid _connectionId = Guid.Parse("3e1c6c1e-1c55-4b7b-9e9a-4a0b3f6f2d13");

    private static SynchronizationOperation Operation(Guid id, int order, string sql = "SELECT 1;", OperationRisk risk = OperationRisk.Low, bool destructive = false, params Guid[] dependsOn) =>
        new(id, order, TableId("T"), destructive ? SynchronizationOperationType.DropObject : SynchronizationOperationType.CreateObject,
            risk, destructive, dependsOn, "Change T", [sql], null, []);

    private static SynchronizationPlan Plan(
        IEnumerable<SynchronizationOperation> operations,
        bool allowDestructive = false,
        EnvironmentLabel environment = EnvironmentLabel.None,
        Guid? id = null) =>
        SynchronizationPlan.Create(
            id ?? _planId, _sessionId, SynchronizationDirection.SourceToTarget, _connectionId, Identity(), environment, "schema-hash",
            new SynchronizationPlanOptions(allowDestructive), operations, [], Now);

    private static readonly Guid _first = Guid.Parse("00000000-0000-0000-0000-000000000001");
    private static readonly Guid _second = Guid.Parse("00000000-0000-0000-0000-000000000002");

    [Fact]
    public void The_content_hash_is_stable_and_covers_every_operation_detail()
    {
        var a = Plan([Operation(_first, 0), Operation(_second, 1, dependsOn: _first)]);
        var b = Plan([Operation(_first, 0), Operation(_second, 1, dependsOn: _first)]);
        var differentSql = Plan([Operation(_first, 0), Operation(_second, 1, sql: "SELECT 2;", dependsOn: _first)]);
        var differentRisk = Plan([Operation(_first, 0), Operation(_second, 1, risk: OperationRisk.Medium, dependsOn: _first)]);

        Assert.Equal(a.ContentHash, b.ContentHash);
        Assert.Equal(a.ContentHash, a.ComputeContentHash());
        Assert.NotEqual(a.ContentHash, differentSql.ContentHash);
        Assert.NotEqual(a.ContentHash, differentRisk.ContentHash);
        Assert.NotEqual(a.ContentHash, Plan([Operation(_first, 0)], id: Guid.NewGuid()).ContentHash);
    }

    [Fact]
    public void A_plan_keeps_its_own_copy_of_operation_lists()
    {
        var sql = new List<string> { "SELECT 1;" };
        var operation = Operation(_first, 0) with { SqlBatches = sql };
        var plan = Plan([operation]);

        sql.Add("DROP TABLE dbo.T;");

        Assert.Single(plan.Operations[0].SqlBatches);
        Assert.Equal(plan.ContentHash, plan.ComputeContentHash());
        Assert.Throws<NotSupportedException>(() => ((IList<string>)plan.Operations[0].SqlBatches).Add("x"));
    }

    [Fact]
    public void Plans_reject_bad_order_forward_dependencies_and_unallowed_destructive_operations()
    {
        Assert.Throws<ArgumentException>(() => Plan([Operation(_first, 1)]));
        Assert.Throws<ArgumentException>(() => Plan([Operation(_first, 0, dependsOn: _second), Operation(_second, 1)]));
        Assert.Throws<ArgumentException>(() => Plan([Operation(_first, 0, destructive: true)]));
        Assert.True(Plan([Operation(_first, 0, destructive: true)], allowDestructive: true).HasDestructiveOperations);
    }

    [Fact]
    public void Approval_needs_the_write_target_confirmed_and_each_risky_operation_approved()
    {
        var plan = Plan([Operation(_first, 0), Operation(_second, 1, risk: OperationRisk.High)]);

        var unconfirmed = PlanApprovalPolicy.Approve(plan, new ApprovalRequest("other", [_second], null), Now);
        var unticked = PlanApprovalPolicy.Approve(plan, new ApprovalRequest(plan.WriteTargetIdentityFingerprint, [], null), Now);
        var approved = PlanApprovalPolicy.Approve(plan, new ApprovalRequest(plan.WriteTargetIdentityFingerprint, [_second], null), Now);

        Assert.Contains(unconfirmed.Errors, error => error == SynchronizationErrors.WriteTargetNotConfirmed);
        Assert.Contains(unticked.Errors, error => error == SynchronizationErrors.OperationNotApproved);
        Assert.True(approved.IsSuccess);
        Assert.Equal(plan.ContentHash, approved.Value.ApprovedContentHash);
        Assert.Equal([_second], approved.Value.ApprovedOperationIds);
    }

    [Fact]
    public void A_production_write_target_needs_its_database_name_typed_exactly()
    {
        var plan = Plan([Operation(_first, 0)], environment: EnvironmentLabel.Production);
        ApprovalRequest Typed(string? name) => new(plan.WriteTargetIdentityFingerprint, [], name);

        Assert.True(PlanApprovalPolicy.RequiresTypedDatabaseName(plan));
        Assert.Contains(PlanApprovalPolicy.Approve(plan, Typed(null), Now).Errors, error => error == SynchronizationErrors.DatabaseNameNotTyped);
        Assert.Contains(PlanApprovalPolicy.Approve(plan, Typed("target"), Now).Errors, error => error == SynchronizationErrors.DatabaseNameNotTyped);
        Assert.True(PlanApprovalPolicy.Approve(plan, Typed("Target"), Now).IsSuccess);
        Assert.False(PlanApprovalPolicy.RequiresTypedDatabaseName(Plan([Operation(_first, 0)], environment: EnvironmentLabel.Staging)));
    }

    [Fact]
    public void An_approval_authorizes_only_its_own_plan_against_the_same_write_target()
    {
        var plan = Plan([Operation(_first, 0)]);
        var approval = PlanApprovalPolicy.Approve(plan, new ApprovalRequest(plan.WriteTargetIdentityFingerprint, [], null), Now).Value;
        var otherPlan = Plan([Operation(_first, 0, sql: "SELECT 2;")]);

        Assert.True(PlanApprovalPolicy.Authorizes(approval, plan, Identity()).IsSuccess);
        Assert.Contains(PlanApprovalPolicy.Authorizes(approval, otherPlan, Identity()).Errors, error => error == SynchronizationErrors.ContentChanged);
        Assert.Contains(PlanApprovalPolicy.Authorizes(approval, Plan([Operation(_first, 0)], id: Guid.NewGuid()), Identity()).Errors, error => error == SynchronizationErrors.WrongPlan);
        Assert.Contains(PlanApprovalPolicy.Authorizes(approval, plan, Identity("Elsewhere")).Errors, error => error == SynchronizationErrors.WriteTargetChanged);
    }

    [Fact]
    public void An_empty_plan_cannot_be_approved()
    {
        var plan = Plan([]);

        Assert.Contains(
            PlanApprovalPolicy.Approve(plan, new ApprovalRequest(plan.WriteTargetIdentityFingerprint, [], null), Now).Errors,
            error => error == SynchronizationErrors.NothingToSynchronize);
    }

    [Fact]
    public void Dependency_ordering_respects_dependencies_then_rank_and_reports_cycles()
    {
        // 0 depends on 1; 2 has the lowest rank; 3 and 4 form a cycle; 5 depends on the cycle.
        int[][] dependencies = [[1], [], [], [4], [3], [3]];
        int[] ranks = [0, 5, 1, 0, 0, 0];

        var order = DependencyOrdering.Sort(dependencies.Length, node => dependencies[node], node => ranks[node]);

        Assert.Equal([2, 1, 0], order.Order);
        Assert.Equal([3, 4], Assert.Single(order.Cycles));
        Assert.Equal([5], order.Blocked);
        Assert.Equal([0], Assert.Single(DependencyOrdering.Sort(1, _ => [0], _ => 0).Cycles));
    }

    [Fact]
    public void Values_survive_the_codec_exactly()
    {
        DatabaseValue[] values =
        [
            DatabaseValue.FromInt64(-42, "bigint"),
            DatabaseValue.FromDecimal(new DecimalValue(System.Numerics.BigInteger.Parse("12345678901234567890123456789012345678", System.Globalization.CultureInfo.InvariantCulture), 10), "decimal"),
            DatabaseValue.FromText("Grüße 𝄞", "nvarchar"),
            DatabaseValue.FromBinary([0, 1, 255], "varbinary"),
            DatabaseValue.FromGuid(Guid.Parse("6f9619ff-8b86-d011-b42d-00c04fc964ff"), "uniqueidentifier"),
            DatabaseValue.FromDateTimeOffset(new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.FromHours(5.5)), "datetimeoffset"),
            DatabaseValue.FromDate(new DateOnly(1, 1, 1), "date"),
            DatabaseValue.Null(ValueKind.Text, "varchar"),
        ];

        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            foreach (var value in values)
            {
                DatabaseValueCodec.Write(writer, value);
            }
        }

        stream.Position = 0;
        using var reader = new BinaryReader(stream);
        foreach (var value in values)
        {
            var read = DatabaseValueCodec.Read(reader);
            Assert.Equal(value.Kind, read.Kind);
            Assert.Equal(value.SqlTypeName, read.SqlTypeName);
            Assert.True(ValueEquality.AreEqual(value, read));
        }
    }

    [Fact]
    public void The_schema_fingerprint_ignores_catalog_order_and_capture_time_but_not_content()
    {
        var a = Database(InSchema("dbo", Table("A", Column("Id")), Table("B", Column("Id"))));
        var reordered = Database(InSchema("dbo", Table("B", Column("Id")), Table("A", Column("Id")))) with { CapturedAtUtc = Now };
        var changed = Database(InSchema("dbo", Table("A", Column("Id")), Table("B", Column("Id", nullable: true))));

        Assert.Equal(SchemaFingerprint.Compute(a), SchemaFingerprint.Compute(reordered));
        Assert.NotEqual(SchemaFingerprint.Compute(a), SchemaFingerprint.Compute(changed));
    }

    [Fact]
    public void Write_and_copy_sides_follow_the_direction()
    {
        Assert.Equal(ComparisonSide.Target, SynchronizationDirection.SourceToTarget.WriteSide());
        Assert.Equal(ComparisonSide.Source, SynchronizationDirection.TargetToSource.WriteSide());
        Assert.Equal(DifferenceStatus.TargetOnly, SynchronizationDirection.TargetToSource.OnlyInCopySide());
    }
}
