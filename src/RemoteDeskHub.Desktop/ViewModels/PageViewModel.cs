using CommunityToolkit.Mvvm.ComponentModel;

namespace RemoteDeskHub.Desktop.ViewModels;

public abstract partial class PageViewModel : ObservableObject
{
    [ObservableProperty]
    private bool isBusy;

    [ObservableProperty]
    private string? statusMessage;

    [ObservableProperty]
    private bool hasError;

    private CancellationTokenSource? _pageCts;

    public abstract string Title { get; }
    public abstract string Subtitle { get; }

    protected CancellationToken PageToken
    {
        get
        {
            _pageCts ??= new CancellationTokenSource();
            return _pageCts.Token;
        }
    }

    public virtual Task LoadAsync()
    {
        return Task.CompletedTask;
    }

    public virtual void OnNavigatedFrom()
    {
        _pageCts?.Cancel();
        _pageCts?.Dispose();
        _pageCts = null;
    }

    protected async Task RunAsync(Func<CancellationToken, Task> action, string? successMessage = null)
    {
        if (IsBusy)
        {
            return;
        }

        IsBusy = true;
        HasError = false;
        StatusMessage = null;
        try
        {
            await action(PageToken);
            StatusMessage = successMessage;
        }
        catch (OperationCanceledException)
        {
            StatusMessage = "Cancelled.";
        }
        catch (Exception ex)
        {
            HasError = true;
            StatusMessage = ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    protected void Fail(string message)
    {
        HasError = true;
        StatusMessage = message;
    }
}
