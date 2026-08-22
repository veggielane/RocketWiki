namespace RocketWiki.Core.Services;

public sealed class PageMutationResult<T>
{
    public T? Value { get; }
    public PageMutationError? Error { get; }

    [System.Diagnostics.CodeAnalysis.MemberNotNullWhen(true, nameof(Value))]
    [System.Diagnostics.CodeAnalysis.MemberNotNullWhen(false, nameof(Error))]
    public bool IsSuccess => Error is null;

    private PageMutationResult(T? value, PageMutationError? error)
    {
        Value = value;
        Error = error;
    }

    public static PageMutationResult<T> Success(T value) => new(value, null);

    public static PageMutationResult<T> Failure(PageMutationError error) => new(default, error);
}
