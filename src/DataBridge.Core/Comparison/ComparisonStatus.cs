namespace DataBridge.Core.Comparison;

/// <summary>Where a comparison is in its lifecycle (domain-model §7); also used for progress.</summary>
public enum ComparisonStatus
{
    Pending,
    Connecting,
    ReadingMetadata,
    ComparingSchema,
    ComparingData,
    Completed,
    Cancelled,
    Failed,
}

/// <summary>One of the two databases being compared.</summary>
public enum ComparisonSide
{
    Source,
    Target,
}
