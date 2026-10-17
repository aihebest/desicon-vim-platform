using Desicon.Vim.Application;
using Desicon.Vim.Application.Cases;
using Desicon.Vim.Domain.Cases;
using Desicon.Vim.Web.Security;
using Microsoft.AspNetCore.Components;

namespace Desicon.Vim.Web.Components.Common;

/// <summary>
/// Base for workbench pages. Blazor Server keeps one DI scope per browser tab
/// for hours, which would keep one database context alive and serve stale
/// data. Instead every operation runs in its own short-lived scope.
/// </summary>
public abstract class VimComponentBase : ComponentBase
{
    [Inject]
    protected IServiceScopeFactory ScopeFactory { get; set; } = default!;

    [Inject]
    protected ICurrentActor CurrentActor { get; set; } = default!;

    [Inject]
    protected ILogger<VimComponentBase> Logger { get; set; } = default!;

    protected Actor? Me { get; private set; }

    /// <summary>Message shown in the page's alert area after an action.</summary>
    protected string? Error { get; set; }

    protected string? Success { get; set; }

    protected bool Busy { get; private set; }

    protected override async Task OnInitializedAsync()
    {
        Me = await CurrentActor.GetAsync().ConfigureAwait(true);
        await LoadAsync().ConfigureAwait(true);
    }

    protected virtual Task LoadAsync() => Task.CompletedTask;

    protected async Task<T> InScope<T>(Func<IServiceProvider, Task<T>> work)
    {
        ArgumentNullException.ThrowIfNull(work);
        await using var scope = ScopeFactory.CreateAsyncScope();
        return await work(scope.ServiceProvider).ConfigureAwait(true);
    }

    protected async Task InScope(Func<IServiceProvider, Task> work)
    {
        ArgumentNullException.ThrowIfNull(work);
        await using var scope = ScopeFactory.CreateAsyncScope();
        await work(scope.ServiceProvider).ConfigureAwait(true);
    }

    /// <summary>Runs a user action, turning business-rule failures into a readable message, then reloads.</summary>
    protected async Task RunActionAsync(Func<IServiceProvider, Task> action, string successMessage)
    {
        ArgumentNullException.ThrowIfNull(action);
        Error = null;
        Success = null;
        Busy = true;
        try
        {
            await InScope(action).ConfigureAwait(true);
            Success = successMessage;
        }
        catch (DomainRuleException ex)
        {
            Error = ex.Message;
        }
        catch (NotFoundException ex)
        {
            Error = ex.Message;
        }
        catch (ConcurrencyConflictException ex)
        {
            Error = ex.Message;
        }
#pragma warning disable CA1031 // Show any unexpected failure to the user instead of breaking the page.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            Logger.LogError(ex, "Workbench action failed.");
            Error = "Something went wrong and the action was not saved. ICT has been notified in the logs.";
        }
        finally
        {
            Busy = false;
        }

        await LoadAsync().ConfigureAwait(true);
    }
}
