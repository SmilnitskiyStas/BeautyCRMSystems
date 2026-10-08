namespace BeautyCrm.Application.Features.BeautyCommon;

/// <summary>Expected business outcomes (mapped to HTTP by the API layer). Exceptions are for infrastructure failures only.</summary>
public enum ErrorKind { Validation, NotFound, Conflict, PaymentFailed, Forbidden }

public sealed record Error(ErrorKind Kind, string Code, string Message)
{
    public static Error Validation(string code, string message) => new(ErrorKind.Validation, code, message);
    public static Error NotFound(string code, string message) => new(ErrorKind.NotFound, code, message);
    public static Error Conflict(string code, string message) => new(ErrorKind.Conflict, code, message);
    public static Error Forbidden(string code, string message) => new(ErrorKind.Forbidden, code, message);
    public static Error PaymentFailed(string code, string message) => new(ErrorKind.PaymentFailed, code, message);
}

public sealed record Result<T>(T? Value, Error? Error)
{
    public static Result<T> Ok(T value) => new(value, null);
    public bool IsOk => Error is null;
    public static implicit operator Result<T>(T value) => new(value, null);
    public static implicit operator Result<T>(Error error) => new(default, error);
}
