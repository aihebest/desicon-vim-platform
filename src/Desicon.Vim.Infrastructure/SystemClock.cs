using Desicon.Vim.Application.Abstractions;

namespace Desicon.Vim.Infrastructure;

/// <summary>The real clock. Tests use a fake <see cref="IClock"/> instead.</summary>
public sealed class SystemClock : IClock
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}
