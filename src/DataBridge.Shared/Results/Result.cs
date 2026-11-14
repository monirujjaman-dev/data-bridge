using DataBridge.Shared.Errors;

namespace DataBridge.Shared.Results;

/// <summary>
/// The outcome of an operation that can fail with expected, user-actionable errors.
/// Unexpected failures are still exceptions.
/// </summary>
public class Result
{
    private static readonly Result _successResult = new([]);

    private protected Result(IReadOnlyList<Error> errors) => Errors = errors;

    /// <summary>The errors; empty when the operation succeeded.</summary>
    public IReadOnlyList<Error> Errors { get; }

    public bool IsSuccess => Errors.Count == 0;

    public bool IsFailure => !IsSuccess;

    public static Result Success() => _successResult;

    public static Result<T> Success<T>(T value) => new(value, []);

    public static Result Failure(Error error) => new(Single(error));

    public static Result Failure(IEnumerable<Error> errors) => new(AtLeastOne(errors));

    public static Result<T> Failure<T>(Error error) => new(default, Single(error));

    public static Result<T> Failure<T>(IEnumerable<Error> errors) => new(default, AtLeastOne(errors));

    private static Error[] Single(Error error)
    {
        ArgumentNullException.ThrowIfNull(error);
        return [error];
    }

    private static Error[] AtLeastOne(IEnumerable<Error> errors)
    {
        ArgumentNullException.ThrowIfNull(errors);
        var list = errors.ToArray();
        if (list.Length == 0)
        {
            throw new ArgumentException("A failure needs at least one error.", nameof(errors));
        }

        if (list.Any(error => error is null))
        {
            throw new ArgumentException("Errors cannot contain null.", nameof(errors));
        }

        return list;
    }
}

/// <summary>A <see cref="Result"/> that carries a value when it succeeds.</summary>
/// <typeparam name="T">The value type.</typeparam>
public sealed class Result<T> : Result
{
    private readonly T? _value;

    internal Result(T? value, IReadOnlyList<Error> errors)
        : base(errors) => _value = value;

    /// <summary>The value. Throws when the result is a failure.</summary>
    public T Value => IsSuccess
        ? _value!
        : throw new InvalidOperationException($"The result is a failure: {string.Join("; ", Errors)}");
}
