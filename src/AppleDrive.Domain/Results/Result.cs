using System.Diagnostics.CodeAnalysis;

namespace AppleDrive.Domain.Results;

/// <summary>Outcome of an operation that can fail in an expected way.</summary>
public readonly struct Result<T>
{
    private readonly T? _value;

    private Result(T value)
    {
        _value = value;
        Error = null;
    }

    private Result(AppError error)
    {
        _value = default;
        Error = error;
    }

    public AppError? Error { get; }

    [MemberNotNullWhen(false, nameof(Error))]
    public bool IsSuccess => Error is null;

    public T Value => IsSuccess
        ? _value!
        : throw new InvalidOperationException($"Result has no value: {Error}");

    public static Result<T> Success(T value) => new(value);

    public static Result<T> Failure(AppError error) => new(error);

    public static implicit operator Result<T>(T value) => Success(value);

    public static implicit operator Result<T>(AppError error) => Failure(error);
}
