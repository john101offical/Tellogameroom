using TelloGameRoom.Core.Protocol;

namespace TelloGameRoom.Server.Infrastructure;

/// <summary>A business-rule failure that maps directly onto a problem response.</summary>
public sealed record ApiError(int Status, string Code, string Detail)
{
    public IResult ToResult() => ApiProblem.Create(Status, Code, Detail);

    public static ApiError NotFound(string detail = "The requested resource does not exist.") => new(404, ErrorCodes.NotFound, detail);

    public static ApiError Conflict(string code, string detail) => new(409, code, detail);

    public static ApiError Invalid(string detail) => new(400, ErrorCodes.Validation, detail);

    public static ApiError Forbidden(string detail = "You do not have permission to do this.") => new(403, ErrorCodes.Forbidden, detail);
}

/// <summary>Either a value or an <see cref="ApiError"/>.</summary>
public readonly struct Result<T>
{
    private Result(T? value, ApiError? error)
    {
        Value = value;
        Error = error;
    }

    public T? Value { get; }

    public ApiError? Error { get; }

    public bool IsSuccess => Error is null;

#pragma warning disable CA2225 // implicit conversions are the whole point of this type
    public static implicit operator Result<T>(T value) => new(value, null);

    public static implicit operator Result<T>(ApiError error) => new(default, error);
#pragma warning restore CA2225

    public IResult ToResult() => Error is null ? Results.Ok(Value) : Error.ToResult();
}
