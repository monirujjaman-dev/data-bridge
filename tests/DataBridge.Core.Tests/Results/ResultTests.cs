using DataBridge.Shared.Errors;
using DataBridge.Shared.Results;

namespace DataBridge.Core.Tests.Results;

// DataBridge.Shared has no test project of its own; its primitives are exercised here through Core.
public sealed class ResultTests
{
    private static readonly Error _someError = new("Test.Error", "Something went wrong.");

    [Fact]
    public void Success_has_value_and_no_errors()
    {
        var result = Result.Success(42);

        Assert.True(result.IsSuccess);
        Assert.Empty(result.Errors);
        Assert.Equal(42, result.Value);
    }

    [Fact]
    public void Failure_value_throws_with_error_codes()
    {
        var result = Result.Failure<int>(_someError);

        Assert.True(result.IsFailure);
        var exception = Assert.Throws<InvalidOperationException>(() => result.Value);
        Assert.Contains("Test.Error", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Failure_requires_at_least_one_error()
    {
        Assert.Throws<ArgumentException>(() => Result.Failure(Array.Empty<Error>()));
        Assert.Throws<ArgumentException>(() => Result.Failure<int>(Array.Empty<Error>()));
    }

    [Fact]
    public void Failure_rejects_null_errors() =>
        Assert.Throws<ArgumentException>(() => Result.Failure<int>(new Error[] { null! }));
}
