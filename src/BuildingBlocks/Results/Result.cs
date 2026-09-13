namespace YourInterview.BuildingBlocks.Results;

/// <summary>
/// 统一结果类型(替代到处 try/catch + 异常控制流)。
/// CQRS 里命令/查询统一返回 Result 或 Result&lt;T&gt;。
/// </summary>
public class Result
{
    protected Result(bool isSuccess, Error error)
    {
        if (isSuccess && error != Error.None) throw new InvalidOperationException("成功的 Result 不能携带错误");
        if (!isSuccess && error == Error.None) throw new InvalidOperationException("失败的 Result 必须携带错误");
        IsSuccess = isSuccess;
        Error = error;
    }

    public bool IsSuccess { get; }
    public bool IsFailure => !IsSuccess;
    public Error Error { get; }

    public static Result Success() => new(true, Error.None);
    public static Result Failure(Error error) => new(false, error);
    public static Result<TValue> Success<TValue>(TValue value) => new(value, true, Error.None);
    public static Result<TValue> Failure<TValue>(Error error) => new(default, false, error);
}

public sealed class Result<TValue> : Result
{
    private readonly TValue? _value;

    internal Result(TValue? value, bool isSuccess, Error error) : base(isSuccess, error) => _value = value;

    public TValue Value => IsSuccess
        ? _value!
        : throw new InvalidOperationException("失败的 Result 不能读取 Value");

    public static implicit operator Result<TValue>(TValue value) => Success(value);
}

/// <summary>错误(带错误码 + 描述 + HTTP 语义),前端可直接映射。</summary>
public sealed record Error(string Code, string Description, ErrorType Type = ErrorType.Failure)
{
    public static readonly Error None = new(string.Empty, string.Empty, ErrorType.None);
    public static Error NotFound(string what) => new($"{what}.NotFound", $"{what} 不存在", ErrorType.NotFound);
    public static Error Validation(string code, string description) => new(code, description, ErrorType.Validation);
    public static Error Conflict(string code, string description) => new(code, description, ErrorType.Conflict);
    public static Error Unauthorized(string description = "未认证") => new("Auth.Unauthorized", description, ErrorType.Unauthorized);
    public static Error Forbidden(string description = "权限不足") => new("Auth.Forbidden", description, ErrorType.Forbidden);
    public static Error Unexpected(string description) => new("Server.Unexpected", description, ErrorType.Unexpected);
}

public enum ErrorType
{
    None = 0,
    Validation = 1,
    NotFound = 2,
    Conflict = 3,
    Unauthorized = 4,
    Forbidden = 5,
    Failure = 6,
    Unexpected = 7
}
