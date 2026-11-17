using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Serilog;
using TelloGameRoom.Core.Client;
using TelloGameRoom.Dashboard.Services;

namespace TelloGameRoom.Dashboard.ViewModels;

/// <summary>Base for pages: a busy flag, an error banner, and a wrapper that turns API failures into readable messages.</summary>
public abstract partial class PageViewModel : ObservableObject
{
    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string? _error;

    [ObservableProperty]
    private string? _notice;

    /// <summary>Called each time the page is shown.</summary>
    public virtual Task ActivateAsync() => Task.CompletedTask;

    [RelayCommand]
    private void DismissError() => Error = null;

    [RelayCommand]
    private void DismissNotice() => Notice = null;

    /// <summary>Runs an action against the server; failures end up in <see cref="Error"/> instead of crashing the app.</summary>
    protected async Task<bool> RunAsync(Func<Task> action, bool showBusy = true)
    {
        Error = null;
        if (showBusy)
        {
            IsBusy = true;
        }

        try
        {
            await action();
            return true;
        }
        catch (ApiException ex)
        {
            Error = Format.Error(ex);
            return false;
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            Log.Warning(ex, "Action failed");
            Error = ex.Message;
            return false;
        }
        finally
        {
            if (showBusy)
            {
                IsBusy = false;
            }
        }
    }
}
