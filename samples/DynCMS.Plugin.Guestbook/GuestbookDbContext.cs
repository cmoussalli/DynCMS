using Microsoft.EntityFrameworkCore;

namespace DynCMS.Plugin.Guestbook;

/// <summary>The plugin's own database: a SQLite file in its data folder, unrelated to the CMS database.</summary>
public sealed class GuestbookDbContext(DbContextOptions<GuestbookDbContext> options) : DbContext(options)
{
    public DbSet<GuestbookEntry> Entries => Set<GuestbookEntry>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<GuestbookEntry>(e =>
        {
            e.ToTable("Entries");
            e.Property(x => x.Name).HasMaxLength(80).IsRequired();
            e.Property(x => x.Message).HasMaxLength(2000).IsRequired();
            e.HasIndex(x => new { x.IsApproved, x.CreatedAt });
        });
    }
}

public sealed class GuestbookEntry
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
    public bool IsApproved { get; set; }
}
