using TelloGameRoom.Core.Protocol;

namespace TelloGameRoom.Server.Infrastructure;

/// <summary>Builds RFC 9457 problem responses that always carry a stable machine-readable <c>code</c>.</summary>
public static class ApiProblem
{
    public static IResult Create(int status, string code, string detail) =>
        Results.Problem(detail: detail, statusCode: status, extensions: new Dictionary<string, object?> { ["code"] = code });

    public static IResult Validation(IDictionary<string, string[]> errors) =>
        Results.ValidationProblem(errors, extensions: new Dictionary<string, object?> { ["code"] = ErrorCodes.Validation });

    public static IResult NotFound(string detail = "The requested resource does not exist.") =>
        Create(StatusCodes.Status404NotFound, ErrorCodes.NotFound, detail);

    public static IResult Conflict(string detail) =>
        Create(StatusCodes.Status409Conflict, ErrorCodes.Conflict, detail);

    public static IResult Forbidden(string detail = "You do not have permission to do this.") =>
        Create(StatusCodes.Status403Forbidden, ErrorCodes.Forbidden, detail);
}

/// <summary>Collects field errors for a request.</summary>
public sealed class Validator
{
    private readonly Dictionary<string, List<string>> _errors = [];

    public bool IsValid => _errors.Count == 0;

    public Validator Check(bool condition, string field, string message)
    {
        if (!condition)
        {
            if (!_errors.TryGetValue(field, out var list))
            {
                _errors[field] = list = [];
            }

            list.Add(message);
        }

        return this;
    }

    public Validator Length(string? value, string field, int min, int max) =>
        Check(value is not null && value.Trim().Length >= min && value.Trim().Length <= max, field,
            $"Must be between {min} and {max} characters.");

    public IResult ToProblem() => ApiProblem.Validation(_errors.ToDictionary(kv => kv.Key, kv => kv.Value.ToArray()));
}
