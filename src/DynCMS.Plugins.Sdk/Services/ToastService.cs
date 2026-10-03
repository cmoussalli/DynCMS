namespace DynCMS.Plugins.Services;

public enum ToastLevel { Info, Success, Warning, Error }

public sealed record ToastMessage(Guid Id, ToastLevel Level, string Title, string? Detail, DateTime CreatedAt);

/// <summary>Scoped notification hub for the admin UI.</summary>
public sealed class ToastService
{
    private readonly List<ToastMessage> _messages = [];

    public IReadOnlyList<ToastMessage> Messages => _messages;

    public event Action? Changed;

    public void Success(string title, string? detail = null) => Add(ToastLevel.Success, title, detail);
    public void Info(string title, string? detail = null) => Add(ToastLevel.Info, title, detail);
    public void Warning(string title, string? detail = null) => Add(ToastLevel.Warning, title, detail);
    public void Error(string title, string? detail = null) => Add(ToastLevel.Error, title, detail);

    public void Error(Exception ex, string? title = null) => Add(ToastLevel.Error, title ?? "Something went wrong", ex.Message);

    public void Dismiss(Guid id)
    {
        if (_messages.RemoveAll(m => m.Id == id) > 0) Changed?.Invoke();
    }

    private void Add(ToastLevel level, string title, string? detail)
    {
        _messages.Add(new ToastMessage(Guid.NewGuid(), level, title, detail, DateTime.UtcNow));
        if (_messages.Count > 5) _messages.RemoveAt(0);
        Changed?.Invoke();
    }
}
