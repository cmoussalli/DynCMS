using DynCMS.Core.Data;
using Microsoft.EntityFrameworkCore;

namespace DynCMS.Core.Analytics;

/// <summary>
/// The analytics store: page views and the geolocation cache. It is a separate context so the tables can live in
/// their own database (<see cref="DatabaseConfiguration.Analytics"/>); when no analytics database is configured
/// the factory points it at the primary database and the tables sit next to the content.
/// </summary>
public class AnalyticsDbContext(DbContextOptions<AnalyticsDbContext> options) : DbContext(options)
{
    public DbSet<PageView> PageViews => Set<PageView>();
    public DbSet<GeoIpEntry> GeoIpEntries => Set<GeoIpEntry>();

    protected override void OnModelCreating(ModelBuilder modelBuilder) => Configure(modelBuilder);

    /// <summary>The analytics tables. Indexed for the reports (time range first) and the log filters; the geolocation cache is keyed by address.</summary>
    public static void Configure(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<PageView>(e =>
        {
            e.ToTable("AnalyticsPageViews");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedOnAdd();
            e.HasIndex(x => x.VisitedAt);
            e.HasIndex(x => new { x.VisitorId, x.VisitedAt });
            e.HasIndex(x => x.SessionId);
            e.HasIndex(x => x.IpAddress);
            e.HasIndex(x => x.GeoResolved);
            e.Property(x => x.Host).HasMaxLength(255);
            e.Property(x => x.Path).HasMaxLength(2000);
            e.Property(x => x.Query).HasMaxLength(1000);
            e.Property(x => x.Title).HasMaxLength(255);
            e.Property(x => x.Culture).HasMaxLength(20);
            e.Property(x => x.Referrer).HasMaxLength(2000);
            e.Property(x => x.ReferrerHost).HasMaxLength(255);
            e.Property(x => x.VisitorId).HasMaxLength(32);
            e.Property(x => x.SessionId).HasMaxLength(32);
            e.Property(x => x.IpAddress).HasMaxLength(45);
            e.Property(x => x.UserAgent).HasMaxLength(1000);
            e.Property(x => x.Browser).HasMaxLength(50);
            e.Property(x => x.BrowserVersion).HasMaxLength(20);
            e.Property(x => x.OperatingSystem).HasMaxLength(50);
            e.Property(x => x.DeviceType).HasMaxLength(20);
            e.Property(x => x.Language).HasMaxLength(20);
            e.Property(x => x.CountryCode).HasMaxLength(2);
            e.Property(x => x.Country).HasMaxLength(100);
            e.Property(x => x.Region).HasMaxLength(100);
            e.Property(x => x.City).HasMaxLength(100);
        });

        modelBuilder.Entity<GeoIpEntry>(e =>
        {
            e.ToTable("AnalyticsGeoIp");
            e.HasKey(x => x.IpAddress);
            e.Property(x => x.IpAddress).HasMaxLength(45);
            e.Property(x => x.CountryCode).HasMaxLength(2);
            e.Property(x => x.Country).HasMaxLength(100);
            e.Property(x => x.Region).HasMaxLength(100);
            e.Property(x => x.City).HasMaxLength(100);
            e.Property(x => x.Timezone).HasMaxLength(50);
            e.Property(x => x.Organization).HasMaxLength(200);
        });
    }

    /// <summary>The table names of this context, for schema checks on a candidate database.</summary>
    public static readonly IReadOnlyList<string> TableNames = ["AnalyticsPageViews", "AnalyticsGeoIp"];

    /// <summary>Builds options for an arbitrary database (used when switching stores and when testing a candidate).</summary>
    public static DbContextOptions<AnalyticsDbContext> OptionsFor(DatabaseConfiguration config, string basePath)
    {
        var builder = new DbContextOptionsBuilder<AnalyticsDbContext>();
        DynCmsDbContextFactory.Apply(builder, config, basePath);
        return builder.Options;
    }
}

/// <summary>
/// Creates <see cref="AnalyticsDbContext"/> instances for the analytics store: the configured analytics database, or
/// the primary one when none is configured. Options are rebuilt whenever the configuration object changes.
/// </summary>
internal sealed class AnalyticsDbContextFactory(DatabaseConfigurationStore store) : IDbContextFactory<AnalyticsDbContext>
{
    private DatabaseConfiguration? _optionsFor;
    private DbContextOptions<AnalyticsDbContext>? _options;

    public AnalyticsDbContext CreateDbContext() => new(GetOptions());

    private DbContextOptions<AnalyticsDbContext> GetOptions()
    {
        var config = store.Resolve(DatabaseRole.Analytics) ?? throw new DynCmsNotConfiguredException();
        var cached = _options;
        if (cached is not null && ReferenceEquals(_optionsFor, config)) return cached;

        var options = AnalyticsDbContext.OptionsFor(config, store.BasePath);
        _optionsFor = config;
        return _options = options;
    }
}
