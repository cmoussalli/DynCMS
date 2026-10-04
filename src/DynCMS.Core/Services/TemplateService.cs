using DynCMS.Core.Data;
using DynCMS.Plugins.Helpers;
using DynCMS.Plugins.Models;
using DynCMS.Core.Templates;
using Microsoft.EntityFrameworkCore;

namespace DynCMS.Core.Services;

public sealed class TemplateService(
    IDbContextFactory<DynCmsDbContext> factory,
    ITemplateRegistry registry,
    ILiquidTemplateEngine engine) : ITemplateService
{
    public async Task<IReadOnlyList<Template>> GetAllAsync(CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await db.Templates.AsNoTracking().OrderBy(t => t.Name).ToListAsync(ct);
    }

    public async Task<Template?> GetAsync(Guid id, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await db.Templates.AsNoTracking().FirstOrDefaultAsync(t => t.Id == id, ct);
    }

    public async Task<Template?> GetByAliasAsync(string alias, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await db.Templates.AsNoTracking().FirstOrDefaultAsync(t => t.Alias == alias, ct);
    }

    public async Task<Template> SaveAsync(Template template, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(template.Name))
            throw new InvalidOperationException("A template needs a name.");
        template.Name = template.Name.Trim();
        if (string.IsNullOrWhiteSpace(template.Alias))
            template.Alias = Slug.ToAlias(template.Name);
        template.Alias = template.Alias.Trim();
        if (string.IsNullOrWhiteSpace(template.Alias))
            throw new InvalidOperationException("A template needs an alias.");
        template.Content ??= string.Empty;

        var syntax = engine.Validate(template.Content);
        if (!syntax.IsValid)
            throw new InvalidOperationException($"The template has a syntax error: {syntax.Error}");

        await using var db = await factory.CreateDbContextAsync(ct);

        if (await db.Templates.AnyAsync(t => t.Alias == template.Alias && t.Id != template.Id, ct))
            throw new InvalidOperationException($"The alias '{template.Alias}' is already used by another template.");

        var existing = await db.Templates.FirstOrDefaultAsync(t => t.Id == template.Id, ct);
        var now = DateTime.UtcNow;

        if (existing is null)
        {
            template.CreatedAt = now;
            template.UpdatedAt = now;
            db.Templates.Add(template);
        }
        else
        {
            // Turning a page template into a partial takes it off the content that still points at it.
            if (existing.Role != TemplateRole.Partial && template.Role == TemplateRole.Partial)
                await DetachFromContentAsync(db, existing.Alias, ct);

            existing.Alias = template.Alias;
            existing.Name = template.Name;
            existing.Description = template.Description;
            existing.Role = template.Role;
            existing.Content = template.Content;
            existing.UpdatedAt = now;
        }

        await db.SaveChangesAsync(ct);
        await RefreshRegistryAsync(ct);
        return (await GetAsync(template.Id, ct))!;
    }

    public async Task<int> CountContentAsync(string alias, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await db.ContentNodes.CountAsync(n => n.TemplateAlias == alias || n.PublishedTemplateAlias == alias, ct);
    }

    public async Task<TemplateUsage> GetUsageAsync(string alias, Guid? ignoreTemplateId = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(alias)) return TemplateUsage.Empty;

        await using var db = await factory.CreateDbContextAsync(ct);

        var contentCount = await db.ContentNodes.CountAsync(n => n.TemplateAlias == alias || n.PublishedTemplateAlias == alias, ct);

        var types = await db.ContentTypes.AsNoTracking().Include(t => t.Properties).ToListAsync(ct);
        var usingTypes = types
            .Where(t => t.AllowedTemplateAliases.Contains(alias, StringComparer.OrdinalIgnoreCase)
                        || string.Equals(t.DefaultTemplateAlias, alias, StringComparison.OrdinalIgnoreCase))
            .OrderBy(t => t.Name)
            .ToList();

        var templates = await db.Templates.AsNoTracking().OrderBy(t => t.Name).ToListAsync(ct);
        var usedBy = templates
            .Where(t => t.Id != ignoreTemplateId && TemplateReferences.Uses(t.Content, alias))
            .ToList();

        var self = templates.FirstOrDefault(t => t.Alias.Equals(alias, StringComparison.OrdinalIgnoreCase));
        IReadOnlyList<string> uses = self is null
            ? []
            : TemplateReferences.Find(self.Content).Where(a => !a.Equals(alias, StringComparison.OrdinalIgnoreCase)).ToList();

        return new TemplateUsage(contentCount, usingTypes, usedBy, uses);
    }

    public async Task DeleteAsync(Guid id, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var template = await db.Templates.FirstOrDefaultAsync(t => t.Id == id, ct);
        if (template is null) return;

        var others = await db.Templates.AsNoTracking().Where(t => t.Id != id).ToListAsync(ct);
        var usedBy = others.Where(t => TemplateReferences.Uses(t.Content, template.Alias)).Select(t => t.Name).ToList();
        if (usedBy.Count > 0)
            throw new InvalidOperationException(
                $"'{template.Name}' is rendered by {string.Join(", ", usedBy)}. Remove the {{% render '{template.Alias}' %}} tag there first.");

        // A component template with the same alias would take over after the delete, so only block when nothing else can render the content.
        var usage = await db.ContentNodes.CountAsync(n => n.TemplateAlias == template.Alias || n.PublishedTemplateAlias == template.Alias, ct);
        if (usage > 0 && registry.GetComponent(template.Alias) is null)
            throw new InvalidOperationException($"'{template.Name}' is used by {usage} content item(s). Assign another template to that content first.");

        db.Templates.Remove(template);

        if (registry.GetComponent(template.Alias) is null)
        {
            // Remove the alias from document types so it no longer shows up as an allowed or default template.
            var types = await db.ContentTypes.ToListAsync(ct);
            foreach (var type in types)
            {
                if (type.AllowedTemplateAliases.RemoveAll(a => a.Equals(template.Alias, StringComparison.OrdinalIgnoreCase)) > 0)
                    type.AllowedTemplateAliases = [.. type.AllowedTemplateAliases];
                if (string.Equals(type.DefaultTemplateAlias, template.Alias, StringComparison.OrdinalIgnoreCase))
                    type.DefaultTemplateAlias = type.AllowedTemplateAliases.FirstOrDefault();
            }
        }

        await db.SaveChangesAsync(ct);
        await RefreshRegistryAsync(ct);
    }

    public async Task<Template> DuplicateAsync(Guid id, CancellationToken ct = default)
    {
        var source = await GetAsync(id, ct) ?? throw new InvalidOperationException("That template no longer exists.");
        var alias = await GetAvailableAliasAsync(source.Alias, ct);
        return await SaveAsync(new Template
        {
            Alias = alias,
            Name = await GetAvailableNameAsync($"{source.Name} copy", ct),
            Description = source.Description,
            Role = source.Role,
            Content = source.Content
        }, ct);
    }

    public async Task<Template> CreateOverrideForComponentAsync(string alias, CancellationToken ct = default)
    {
        var component = registry.GetComponent(alias)
            ?? throw new InvalidOperationException($"There is no component template with the alias '{alias}'.");

        if (await GetByAliasAsync(alias, ct) is not null)
            throw new InvalidOperationException($"An editable template with the alias '{alias}' already exists.");

        await using var db = await factory.CreateDbContextAsync(ct);
        var types = await db.ContentTypes.AsNoTracking().Include(t => t.Properties).ToListAsync(ct);

        // Scaffold from the document type that uses this template, so every property is already in the markup.
        var type = types.FirstOrDefault(t => string.Equals(t.DefaultTemplateAlias, alias, StringComparison.OrdinalIgnoreCase))
                   ?? types.FirstOrDefault(t => t.AllowedTemplateAliases.Contains(alias, StringComparer.OrdinalIgnoreCase));

        return await SaveAsync(new Template
        {
            Alias = alias,
            Name = component.Name,
            Role = TemplateRole.Page,
            Description = $"Editable version of the {component.ComponentType?.Name ?? alias} component template. " +
                          "Content using this alias renders this markup instead; delete it to fall back to the component.",
            Content = TemplateSamples.Scaffold(type)
        }, ct);
    }

    public async Task<string> GetAvailableAliasAsync(string preferred, CancellationToken ct = default)
    {
        var baseAlias = Slug.ToAlias(preferred);
        if (string.IsNullOrWhiteSpace(baseAlias)) baseAlias = "template";

        await using var db = await factory.CreateDbContextAsync(ct);
        var taken = (await db.Templates.AsNoTracking().Select(t => t.Alias).ToListAsync(ct))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (!taken.Contains(baseAlias)) return baseAlias;
        for (var i = 2; ; i++)
        {
            var candidate = $"{baseAlias}{i}";
            if (!taken.Contains(candidate)) return candidate;
        }
    }

    private async Task<string> GetAvailableNameAsync(string preferred, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var taken = (await db.Templates.AsNoTracking().Select(t => t.Name).ToListAsync(ct))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (!taken.Contains(preferred)) return preferred;
        for (var i = 2; ; i++)
        {
            var candidate = $"{preferred} {i}";
            if (!taken.Contains(candidate)) return candidate;
        }
    }

    /// <summary>Clears the template alias from content that points at it (used when a page template becomes a partial).</summary>
    private static async Task DetachFromContentAsync(DynCmsDbContext db, string alias, CancellationToken ct)
    {
        var nodes = await db.ContentNodes
            .Where(n => n.TemplateAlias == alias || n.PublishedTemplateAlias == alias)
            .ToListAsync(ct);
        foreach (var node in nodes)
        {
            if (string.Equals(node.TemplateAlias, alias, StringComparison.OrdinalIgnoreCase)) node.TemplateAlias = null;
            if (string.Equals(node.PublishedTemplateAlias, alias, StringComparison.OrdinalIgnoreCase)) node.PublishedTemplateAlias = null;
        }

        var types = await db.ContentTypes.ToListAsync(ct);
        foreach (var type in types)
        {
            if (type.AllowedTemplateAliases.RemoveAll(a => a.Equals(alias, StringComparison.OrdinalIgnoreCase)) > 0)
                type.AllowedTemplateAliases = [.. type.AllowedTemplateAliases];
            if (string.Equals(type.DefaultTemplateAlias, alias, StringComparison.OrdinalIgnoreCase))
                type.DefaultTemplateAlias = type.AllowedTemplateAliases.FirstOrDefault();
        }
    }

    public async Task RefreshRegistryAsync(CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        registry.SetStored(await db.Templates.AsNoTracking().ToListAsync(ct));
    }
}
