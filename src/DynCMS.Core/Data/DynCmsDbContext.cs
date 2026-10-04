using System.Text.Json;
using DynCMS.Core.Api;
using DynCMS.Core.Services;
using DynCMS.Plugins.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace DynCMS.Core.Data;

public class DynCmsDbContext(DbContextOptions<DynCmsDbContext> options) : DbContext(options)
{
    public DbSet<ContentType> ContentTypes => Set<ContentType>();
    public DbSet<PropertyType> PropertyTypes => Set<PropertyType>();
    public DbSet<ContentNode> ContentNodes => Set<ContentNode>();
    public DbSet<MediaItem> MediaItems => Set<MediaItem>();
    public DbSet<Template> Templates => Set<Template>();
    public DbSet<ApiKey> ApiKeys => Set<ApiKey>();
    public DbSet<Language> Languages => Set<Language>();
    public DbSet<DictionaryItem> DictionaryItems => Set<DictionaryItem>();
    public DbSet<SettingEntry> Settings => Set<SettingEntry>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        var stringList = JsonConversion<List<string>>(() => []);
        var stringMap = JsonConversion<Dictionary<string, string>>(() => new(StringComparer.OrdinalIgnoreCase));
        var valueMap = JsonConversion<Dictionary<string, string?>>(() => new(StringComparer.OrdinalIgnoreCase));
        var cultureMap = CultureMapConversion();

        modelBuilder.Entity<ContentType>(e =>
        {
            e.ToTable("ContentTypes");
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.Alias).IsUnique();
            e.Property(x => x.Alias).HasMaxLength(100);
            e.Property(x => x.Name).HasMaxLength(200);
            e.Property(x => x.Icon).HasMaxLength(50);
            e.Property(x => x.AllowedChildTypeAliases).HasConversion(stringList.Converter, stringList.Comparer);
            e.Property(x => x.AllowedTemplateAliases).HasConversion(stringList.Converter, stringList.Comparer);
            e.HasMany(x => x.Properties).WithOne().HasForeignKey(p => p.ContentTypeId).OnDelete(DeleteBehavior.Cascade);
            e.Ignore(x => x.Groups);
        });

        modelBuilder.Entity<PropertyType>(e =>
        {
            e.ToTable("PropertyTypes");
            e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.ContentTypeId, x.Alias }).IsUnique();
            e.Property(x => x.Alias).HasMaxLength(100);
            e.Property(x => x.Name).HasMaxLength(200);
            e.Property(x => x.EditorAlias).HasMaxLength(100);
            e.Property(x => x.GroupName).HasMaxLength(100);
            e.Property(x => x.Config).HasConversion(stringMap.Converter, stringMap.Comparer);
        });

        modelBuilder.Entity<ContentNode>(e =>
        {
            e.ToTable("ContentNodes");
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.ParentId);
            e.HasIndex(x => x.Path);
            e.HasIndex(x => new { x.ParentId, x.UrlSegment });
            e.Property(x => x.Name).HasMaxLength(255);
            e.Property(x => x.UrlSegment).HasMaxLength(255);
            e.Property(x => x.DraftValues).HasConversion(valueMap.Converter, valueMap.Comparer);
            e.Property(x => x.PublishedValues).HasConversion(valueMap.Converter, valueMap.Comparer);
            // Per-language state as JSON; an empty string (the default for rows that predate languages) is an empty map.
            e.Property(x => x.Cultures).HasConversion(cultureMap.Converter, cultureMap.Comparer);
            e.HasOne(x => x.ContentType).WithMany().HasForeignKey(x => x.ContentTypeId).OnDelete(DeleteBehavior.Restrict);
            e.Ignore(x => x.AncestorIds);
            e.Ignore(x => x.HasPendingChanges);
            e.Ignore(x => x.VariesByCulture);
            e.Ignore(x => x.ExistingCultures);
            e.Ignore(x => x.PublishedCultures);
        });

        modelBuilder.Entity<Language>(e =>
        {
            e.ToTable("Languages");
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.IsoCode).IsUnique();
            e.Property(x => x.IsoCode).HasMaxLength(20);
            e.Property(x => x.Name).HasMaxLength(200);
            e.Property(x => x.FallbackIsoCode).HasMaxLength(20);
            e.Ignore(x => x.UrlPrefix);
            e.Ignore(x => x.Culture);
        });

        modelBuilder.Entity<DictionaryItem>(e =>
        {
            e.ToTable("DictionaryItems");
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.Key).IsUnique();
            e.HasIndex(x => x.ParentId);
            e.Property(x => x.Key).HasMaxLength(DictionaryItem.MaxKeyLength);
            // One text per language as JSON, like the per-language values of a content node.
            e.Property(x => x.Translations).HasConversion(valueMap.Converter, valueMap.Comparer);
            e.Ignore(x => x.Level);
            e.Ignore(x => x.TranslatedCultures);
        });

        modelBuilder.Entity<MediaItem>(e =>
        {
            e.ToTable("MediaItems");
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.ParentId);
            e.Property(x => x.Name).HasMaxLength(255);
            e.Ignore(x => x.IsImage);
        });

        modelBuilder.Entity<Template>(e =>
        {
            e.ToTable("Templates");
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.Alias).IsUnique();
            e.Property(x => x.Alias).HasMaxLength(100);
            e.Property(x => x.Name).HasMaxLength(200);
            e.Property(x => x.Description).HasMaxLength(1000);
            // Stored as text and defaulted in the database so the column can be added to an existing
            // Templates table (every template that predates partials is a page template).
            e.Property(x => x.Role).HasConversion<string>().HasMaxLength(20).HasDefaultValue(TemplateRole.Page);
            e.Ignore(x => x.IsPartial);
        });

        modelBuilder.Entity<ApiKey>(e =>
        {
            e.ToTable("ApiKeys");
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.Hash).IsUnique();
            e.HasIndex(x => x.UserId);
            e.Property(x => x.Name).HasMaxLength(200);
            e.Property(x => x.Prefix).HasMaxLength(32);
            e.Property(x => x.Hash).HasMaxLength(64);
            e.Property(x => x.UserId).HasMaxLength(100);
            e.Property(x => x.UserName).HasMaxLength(200);
            e.Property(x => x.Scopes).HasConversion(stringList.Converter, stringList.Comparer);
            e.Ignore(x => x.IsRevoked);
            e.Ignore(x => x.IsExpired);
            e.Ignore(x => x.IsActive);
        });

        modelBuilder.Entity<SettingEntry>(e =>
        {
            e.ToTable("Settings");
            e.HasKey(x => x.Key);
            e.Property(x => x.Key).HasMaxLength(SettingEntry.MaxKeyLength);
        });
    }

    /// <summary>
    /// The per-language map of a content node. Keys (ISO codes) and the value dictionaries inside are re-wrapped
    /// to ignore case after deserialisation, which <see cref="JsonSerializer"/> does not do on its own.
    /// </summary>
    private static (ValueConverter<Dictionary<string, ContentCulture>, string> Converter, ValueComparer<Dictionary<string, ContentCulture>> Comparer) CultureMapConversion()
    {
        var converter = new ValueConverter<Dictionary<string, ContentCulture>, string>(
            v => JsonSerializer.Serialize(v, (JsonSerializerOptions?)null),
            v => ReadCultures(v));

        var comparer = new ValueComparer<Dictionary<string, ContentCulture>>(
            (a, b) => JsonSerializer.Serialize(a, (JsonSerializerOptions?)null) == JsonSerializer.Serialize(b, (JsonSerializerOptions?)null),
            v => JsonSerializer.Serialize(v, (JsonSerializerOptions?)null).GetHashCode(),
            v => ReadCultures(JsonSerializer.Serialize(v, (JsonSerializerOptions?)null)));

        return (converter, comparer);
    }

    private static Dictionary<string, ContentCulture> ReadCultures(string? json)
    {
        var result = new Dictionary<string, ContentCulture>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(json)) return result;
        var raw = JsonSerializer.Deserialize<Dictionary<string, ContentCulture>>(json, (JsonSerializerOptions?)null);
        if (raw is null) return result;
        foreach (var (key, culture) in raw)
        {
            culture.NormalizeComparers();
            result[key] = culture;
        }
        return result;
    }

    private static (ValueConverter<T, string> Converter, ValueComparer<T> Comparer) JsonConversion<T>(Func<T> empty) where T : class
    {
        var converter = new ValueConverter<T, string>(
            v => JsonSerializer.Serialize(v, (JsonSerializerOptions?)null),
            v => string.IsNullOrWhiteSpace(v) ? empty() : (JsonSerializer.Deserialize<T>(v, (JsonSerializerOptions?)null) ?? empty()));

        var comparer = new ValueComparer<T>(
            (a, b) => JsonSerializer.Serialize(a, (JsonSerializerOptions?)null) == JsonSerializer.Serialize(b, (JsonSerializerOptions?)null),
            v => JsonSerializer.Serialize(v, (JsonSerializerOptions?)null).GetHashCode(),
            v => JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(v, (JsonSerializerOptions?)null), (JsonSerializerOptions?)null) ?? empty());

        return (converter, comparer);
    }
}
