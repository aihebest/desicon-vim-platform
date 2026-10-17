namespace Desicon.Vim.Application;

/// <summary>
/// Two people changed the same case at the same time and this save lost the
/// race (optimistic concurrency via the case's RowVersion). Nothing was saved;
/// the user should reload the case and try again.
/// </summary>
public sealed class ConcurrencyConflictException : Exception
{
    public const string DefaultMessage = "This case was changed by someone else. Reload and try again.";

    public ConcurrencyConflictException()
        : base(DefaultMessage)
    {
    }

    public ConcurrencyConflictException(string message)
        : base(message)
    {
    }

    public ConcurrencyConflictException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
