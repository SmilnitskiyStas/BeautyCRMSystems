namespace BeautyCrm.Application.Features.BeautyCommon;

/// <summary>Expected business outcomes (mapped to HTTP by the API layer). Exceptions are for infrastructure failures only.</summary>
public enum ErrorKind { Validation, NotFound, Conflict, PaymentFailed, Forbidden, TooManyRequests }

/// <param name="Conflicts">Додаткові дані 409 (напр. список записів, що блокують зміну вихідних, §17); у тіло відповіді як `conflicts`.</param>
/// <param name="Extras">Додаткові поля тіла помилки (§30.11), напр. <c>permission</c>/<c>required</c> для 403 forbidden_role.</param>
public sealed record Error(ErrorKind Kind, string Code, string Message, object? Conflicts = null, IReadOnlyDictionary<string, object?>? Extras = null)
{
    /// <summary>403 forbidden_role з <c>permission</c> і <c>required</c> (відмова за правом).</summary>
    public static Error ForbiddenPermission(string permission, string required) =>
        new(ErrorKind.Forbidden, "forbidden_role", "This action is not available for your access level.", null,
            new Dictionary<string, object?> { ["permission"] = permission, ["required"] = required });

    public static Error ConflictWith(string code, string message, object conflicts) => new(ErrorKind.Conflict, code, message, conflicts);
    public static Error Validation(string code, string message) => new(ErrorKind.Validation, code, message);
    public static Error NotFound(string code, string message) => new(ErrorKind.NotFound, code, message);
    public static Error Conflict(string code, string message) => new(ErrorKind.Conflict, code, message);
    public static Error Forbidden(string code, string message) => new(ErrorKind.Forbidden, code, message);
    public static Error TooMany(string code, string message) => new(ErrorKind.TooManyRequests, code, message);
    public static Error PaymentFailed(string code, string message) => new(ErrorKind.PaymentFailed, code, message);
}

public sealed record Result<T>(T? Value, Error? Error)
{
    public static Result<T> Ok(T value) => new(value, null);
    public bool IsOk => Error is null;
    public static implicit operator Result<T>(T value) => new(value, null);
    public static implicit operator Result<T>(Error error) => new(default, error);
}
