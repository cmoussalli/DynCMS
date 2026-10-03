using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DynCMS.Plugin.Guestbook;

/// <summary>
/// A scoped plugin service. Its dependencies come from both sides: the DbContext factory and the options are the
/// plugin's own registrations, the logger comes from the host.
/// </summary>
public sealed class GuestbookService(IDbContextFactory<GuestbookDbContext> factory, IOptions<GuestbookOptions> options, ILogger<GuestbookService> logger)
{
    public GuestbookOptions Options => options.Value;

    public async Task<IReadOnlyList<GuestbookEntry>> GetApprovedAsync(CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await db.Entries.AsNoTracking().Where(e => e.IsApproved).OrderByDescending(e => e.CreatedAt).Take(200).ToListAsync(ct);
    }

    public async Task<IReadOnlyList<GuestbookEntry>> GetAllAsync(CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await db.Entries.AsNoTracking().OrderBy(e => e.IsApproved).ThenByDescending(e => e.CreatedAt).ToListAsync(ct);
    }

    public async Task<GuestbookEntry?> GetAsync(int id, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await db.Entries.AsNoTracking().FirstOrDefaultAsync(e => e.Id == id, ct);
    }

    public async Task<GuestbookEntry> SignAsync(string? name, string? message, CancellationToken ct = default)
    {
        name = (name ?? string.Empty).Trim();
        message = (message ?? string.Empty).Trim();
        if (name.Length is 0 or > 80) throw new ArgumentException("Please enter a name (up to 80 characters).");
        if (message.Length == 0) throw new ArgumentException("Please write a message.");
        if (message.Length > Options.MaxMessageLength) throw new ArgumentException($"Messages are limited to {Options.MaxMessageLength} characters.");

        var entry = new GuestbookEntry { Name = name, Message = message, CreatedAt = DateTime.UtcNow, IsApproved = !Options.RequireApproval };
        await using var db = await factory.CreateDbContextAsync(ct);
        db.Entries.Add(entry);
        await db.SaveChangesAsync(ct);
        logger.LogInformation("Guestbook entry {Id} by {Name} ({State})", entry.Id, entry.Name, entry.IsApproved ? "published" : "awaiting approval");
        return entry;
    }

    public async Task<bool> SetApprovedAsync(int id, bool approved, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var entry = await db.Entries.FindAsync([id], ct);
        if (entry is null) return false;
        entry.IsApproved = approved;
        await db.SaveChangesAsync(ct);
        return true;
    }

    public async Task<bool> DeleteAsync(int id, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var entry = await db.Entries.FindAsync([id], ct);
        if (entry is null) return false;
        db.Entries.Remove(entry);
        await db.SaveChangesAsync(ct);
        return true;
    }

    public async Task<GuestbookStats> GetStatsAsync(CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var total = await db.Entries.CountAsync(ct);
        var approved = await db.Entries.CountAsync(e => e.IsApproved, ct);
        var latest = await db.Entries.OrderByDescending(e => e.CreatedAt).Select(e => (DateTime?)e.CreatedAt).FirstOrDefaultAsync(ct);
        return new GuestbookStats(total, approved, total - approved, latest);
    }
}

public sealed record GuestbookStats(int Total, int Approved, int Pending, DateTime? LatestAt);
