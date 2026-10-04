namespace DynCMS.Plugin.Guestbook;

/// <summary>
/// What the site's editors did lately, as the plugin saw it through <see cref="ICmsEventHandler"/>. A singleton: the
/// event handler writes to it, the dashboard widget reads it.
/// </summary>
public sealed class ActivityLog
{
    private readonly Queue<string> _lines = new();

    public void Add(string line)
    {
        lock (_lines)
        {
            _lines.Enqueue($"{DateTime.Now:HH:mm:ss} {line}");
            while (_lines.Count > 5) _lines.Dequeue();
        }
    }

    public IReadOnlyList<string> Latest()
    {
        lock (_lines) return _lines.Reverse().ToList();
    }
}

/// <summary>
/// Reacts to changes in the CMS. Registered with <c>services.AddSingleton&lt;ICmsEventHandler, GuestbookEvents&gt;()</c>;
/// it is called while the plugin runs, after the change is saved.
/// </summary>
public sealed class GuestbookEvents(ActivityLog log) : ICmsEventHandler
{
    public Task OnContentAsync(ContentEvent e, CancellationToken ct)
    {
        var languages = e.Cultures is { Count: > 0 } ? $" ({string.Join(", ", e.Cultures)})" : string.Empty;
        log.Add($"{e.Kind}: {e.Node.Name}{languages}");
        return Task.CompletedTask;
    }

    public Task OnMediaAsync(MediaEvent e, CancellationToken ct)
    {
        log.Add($"Media {e.Kind}: {e.Item.Name}");
        return Task.CompletedTask;
    }
}

/// <summary>Formats an entry for display. Two implementations are registered under keys ("short" and "long"): keyed services work inside plugins.</summary>
public interface IEntryFormatter
{
    string Format(GuestbookEntry entry);
}

public sealed class ShortEntryFormatter : IEntryFormatter
{
    public string Format(GuestbookEntry entry) => $"{entry.Name}: {Trim(entry.Message, 40)}";

    private static string Trim(string text, int max) => text.Length <= max ? text : text[..max].TrimEnd() + "…";
}

public sealed class LongEntryFormatter : IEntryFormatter
{
    public string Format(GuestbookEntry entry) => $"{entry.Name} wrote on {entry.CreatedAt.ToLocalTime():d MMMM yyyy}: {entry.Message}";
}
