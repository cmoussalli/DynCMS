using System.Collections.Concurrent;
using System.Globalization;
using System.Net.Mail;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace DynCMS.Plugin.Forms;

public sealed record FormSummary(FormDefinition Form, int Fields, int Submissions, DateTime? LastSubmissionAt);

/// <summary>What happened to a submission: stored, or refused with a message per field.</summary>
public sealed record SubmitResult(bool Ok, string? Message = null, IReadOnlyDictionary<string, string>? Errors = null, int StatusCode = 200)
{
    public static SubmitResult Success() => new(true);
    public static SubmitResult Invalid(IReadOnlyDictionary<string, string> errors) => new(false, "Please check the highlighted fields.", errors, 400);
    public static SubmitResult Refused(string message, int status) => new(false, message, null, status);
}

/// <summary>
/// Forms and their submissions. Scoped. Besides the plugin's own database it keeps one stored Liquid partial template
/// per form (<c>form-{alias}</c>) in step with the form, which is how a form gets into the website.
/// </summary>
public sealed partial class FormsService(
    IDbContextFactory<FormsDbContext> factory,
    ITemplateService templates,
    ILogger<FormsService> logger)
{
    public const string ManagedMarker = "Managed by the Forms plugin";

    [GeneratedRegex("^[a-z0-9]+(-[a-z0-9]+)*$")]
    private static partial Regex AliasPattern();

    [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_]*$")]
    private static partial Regex NamePattern();

    // ---- forms ----------------------------------------------------------------------------------------

    public async Task<IReadOnlyList<FormSummary>> GetSummariesAsync(CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var forms = await db.Forms.AsNoTracking().OrderBy(f => f.Name).ToListAsync(ct);
        var stats = await db.Submissions.AsNoTracking().GroupBy(s => s.FormId)
            .Select(g => new { g.Key, Count = g.Count(), Last = g.Max(s => s.CreatedAt) }).ToDictionaryAsync(x => x.Key, ct);
        return forms.Select(f => new FormSummary(f, f.GetFields().Count,
            stats.TryGetValue(f.Id, out var s) ? s.Count : 0, stats.TryGetValue(f.Id, out s) ? s.Last : null)).ToList();
    }

    public async Task<FormDefinition?> GetAsync(Guid id, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await db.Forms.AsNoTracking().FirstOrDefaultAsync(f => f.Id == id, ct);
    }

    public async Task<FormDefinition?> GetByAliasAsync(string alias, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await db.Forms.AsNoTracking().FirstOrDefaultAsync(f => f.Alias == alias, ct);
    }

    /// <summary>A new form with a sensible starting set of fields; it is not saved until <see cref="SaveAsync"/>.</summary>
    public static FormDefinition NewForm()
    {
        var form = new FormDefinition { Name = "Contact form", Alias = "contact" };
        form.SetFields(
        [
            new FormField { Name = "name", Label = "Your name", Type = FieldType.Text, Required = true },
            new FormField { Name = "email", Label = "Email address", Type = FieldType.Email, Required = true },
            new FormField { Name = "message", Label = "Message", Type = FieldType.TextArea, Required = true }
        ]);
        return form;
    }

    /// <summary>
    /// Validates and saves a form (insert when its id is unknown), then rewrites its partial view. Throws
    /// <see cref="InvalidOperationException"/> with a message for the editor.
    /// </summary>
    public async Task<FormDefinition> SaveAsync(FormDefinition form, CancellationToken ct = default)
    {
        form.Name = (form.Name ?? string.Empty).Trim();
        form.Alias = Slug.ToUrlSegment(form.Alias);
        form.SubmitLabel = string.IsNullOrWhiteSpace(form.SubmitLabel) ? "Send" : form.SubmitLabel.Trim();
        form.SuccessMessage = string.IsNullOrWhiteSpace(form.SuccessMessage) ? "Thank you!" : form.SuccessMessage.Trim();
        form.RedirectUrl = string.IsNullOrWhiteSpace(form.RedirectUrl) ? null : form.RedirectUrl.Trim();
        form.Description = string.IsNullOrWhiteSpace(form.Description) ? null : form.Description.Trim();
        form.Theme = FormThemes.Normalize(form.Theme);
        form.CustomCss = string.IsNullOrWhiteSpace(form.CustomCss) ? null : form.CustomCss.Trim();
        if (form.CustomCss is not null && FormHtml.CustomCssError(form.CustomCss) is { } cssError) throw new InvalidOperationException(cssError);

        if (form.Name.Length is 0 or > 120) throw new InvalidOperationException("Give the form a name (up to 120 characters).");
        if (form.Alias.Length is 0 or > 60 || !AliasPattern().IsMatch(form.Alias)) throw new InvalidOperationException("The alias needs letters or digits, e.g. 'contact' (up to 60 characters).");
        if (form.RedirectUrl is { } redirect && !(redirect.StartsWith('/') && !redirect.StartsWith("//", StringComparison.Ordinal)
                                                  || Uri.TryCreate(redirect, UriKind.Absolute, out var u) && u.Scheme is "http" or "https"))
            throw new InvalidOperationException("The redirect must be a path such as /thanks or an http(s) address.");

        var fields = NormalizeFields(form.GetFields());
        form.SetFields(fields);
        form.UpdatedAt = DateTime.UtcNow;

        await using var db = await factory.CreateDbContextAsync(ct);
        var existing = await db.Forms.FirstOrDefaultAsync(f => f.Id == form.Id, ct);
        if (await db.Forms.AnyAsync(f => f.Alias == form.Alias && f.Id != form.Id, ct))
            throw new InvalidOperationException($"Another form already uses the alias '{form.Alias}'.");

        if (existing is null)
        {
            form.CreatedAt = DateTime.UtcNow;
            db.Forms.Add(form);
        }
        else
        {
            // The partial is named after the alias; renaming it would orphan the templates that render it.
            if (!string.Equals(existing.Alias, form.Alias, StringComparison.Ordinal))
                throw new InvalidOperationException("The alias of a saved form cannot change: page templates refer to it. Duplicate the form instead.");
            existing.Name = form.Name;
            existing.Description = form.Description;
            existing.SubmitLabel = form.SubmitLabel;
            existing.SuccessMessage = form.SuccessMessage;
            existing.RedirectUrl = form.RedirectUrl;
            existing.IsEnabled = form.IsEnabled;
            existing.Theme = form.Theme;
            existing.CustomCss = form.CustomCss;
            existing.FieldsJson = form.FieldsJson;
            existing.UpdatedAt = form.UpdatedAt;
            form = existing;
        }

        // The partial first: when the template service refuses (alias taken by an unrelated template) nothing is saved.
        await WritePartialAsync(form, ct);
        await db.SaveChangesAsync(ct);
        return form;
    }

    public async Task DeleteAsync(Guid id, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var form = await db.Forms.FirstOrDefaultAsync(f => f.Id == id, ct);
        if (form is null) return;

        var partial = await templates.GetByAliasAsync(form.PartialAlias, ct);
        if (partial is not null)
        {
            var usage = await templates.GetUsageAsync(partial.Alias, partial.Id, ct);
            if (usage.UsedBy.Count > 0)
                throw new InvalidOperationException($"The form is still rendered by the template(s) {string.Join(", ", usage.UsedBy.Select(t => $"'{t.Name}'"))}. Remove {{% render '{partial.Alias}' %}} there first.");
            await templates.DeleteAsync(partial.Id, ct);
        }

        db.Submissions.RemoveRange(db.Submissions.Where(s => s.FormId == id));
        db.Forms.Remove(form);
        await db.SaveChangesAsync(ct);
    }

    public async Task<FormDefinition> DuplicateAsync(Guid id, CancellationToken ct = default)
    {
        var source = await GetAsync(id, ct) ?? throw new InvalidOperationException("The form no longer exists.");
        await using var db = await factory.CreateDbContextAsync(ct);
        var alias = source.Alias;
        var n = 2;
        while (await db.Forms.AnyAsync(f => f.Alias == $"{source.Alias}-{n}", ct)) n++;
        alias = $"{source.Alias}-{n}";
        var copy = new FormDefinition
        {
            Name = $"{source.Name} (copy)", Alias = alias, Description = source.Description, SubmitLabel = source.SubmitLabel,
            SuccessMessage = source.SuccessMessage, RedirectUrl = source.RedirectUrl, IsEnabled = source.IsEnabled, FieldsJson = source.FieldsJson, Theme = source.Theme, CustomCss = source.CustomCss
        };
        return await SaveAsync(copy, ct);
    }

    private static List<FormField> NormalizeFields(List<FormField> fields)
    {
        if (fields.Count == 0) throw new InvalidOperationException("Add at least one field.");
        if (fields.Count > 60) throw new InvalidOperationException("A form can have up to 60 fields.");

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "_hp" };
        foreach (var f in fields)
        {
            f.Label = (f.Label ?? string.Empty).Trim();
            if (f.Label.Length == 0) throw new InvalidOperationException("Every field needs a label.");
            f.Name = string.IsNullOrWhiteSpace(f.Name) ? Slug.ToAlias(f.Label) : f.Name.Trim();
            if (f.Name.Length == 0 || !NamePattern().IsMatch(f.Name))
                throw new InvalidOperationException($"The field name '{f.Name}' needs letters, digits or underscores and must not start with a digit.");
            if (!seen.Add(f.Name)) throw new InvalidOperationException($"Two fields are called '{f.Name}'. Field names must be unique.");
            f.Placeholder = string.IsNullOrWhiteSpace(f.Placeholder) ? null : f.Placeholder.Trim();
            f.HelpText = string.IsNullOrWhiteSpace(f.HelpText) ? null : f.HelpText.Trim();
            f.MaxLength = Math.Clamp(f.MaxLength, 0, 10_000);
            f.Options = f.HasOptions ? f.Options.Select(o => o.Trim()).Where(o => o.Length > 0).Distinct().ToList() : [];
            if (f.HasOptions && f.Options.Count == 0) throw new InvalidOperationException($"'{f.Label}' needs at least one option.");
        }
        return fields;
    }

    // ---- partial views --------------------------------------------------------------------------------

    private async Task WritePartialAsync(FormDefinition form, CancellationToken ct)
    {
        var alias = form.PartialAlias;
        var template = await templates.GetByAliasAsync(alias, ct);
        if (template is not null && !template.IsPartial)
            throw new InvalidOperationException($"A page template with the alias '{alias}' exists already; choose another form alias.");

        template ??= new Template { Alias = alias, Role = TemplateRole.Partial };
        template.Name = $"Form: {form.Name}";
        template.Description = $"{ManagedMarker}: edit the form under Forms, not here (changes are overwritten). Use {{% render '{alias}' %}}.";
        template.Content = $"{{% comment %}}{ManagedMarker}. Do not edit.{{% endcomment %}}\n{FormHtml.Render(form)}";
        await templates.SaveAsync(template, ct);
    }

    /// <summary>
    /// Rewrites every form's partial: on start with the live markup, on stop with an empty comment, so pages that
    /// render a form keep working (without it) while the plugin is not running.
    /// </summary>
    public async Task SyncPartialsAsync(bool live, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        foreach (var form in await db.Forms.AsNoTracking().ToListAsync(ct))
        {
            try
            {
                if (live)
                {
                    await WritePartialAsync(form, ct);
                }
                else if (await templates.GetByAliasAsync(form.PartialAlias, ct) is { } t && t.IsPartial)
                {
                    t.Content = $"{{% comment %}}{ManagedMarker}: the Forms plugin is not running.{{% endcomment %}}";
                    await templates.SaveAsync(t, ct);
                }
            }
            catch (Exception ex) when (ex is InvalidOperationException or DbUpdateException)
            {
                logger.LogWarning(ex, "Could not update the partial view of form {Alias}", form.Alias);
            }
        }
    }

    // ---- submissions ----------------------------------------------------------------------------------

    public async Task<IReadOnlyList<FormSubmission>> GetSubmissionsAsync(Guid formId, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await db.Submissions.AsNoTracking().Where(s => s.FormId == formId).OrderByDescending(s => s.CreatedAt).Take(2000).ToListAsync(ct);
    }

    public async Task DeleteSubmissionAsync(int id, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        await db.Submissions.Where(s => s.Id == id).ExecuteDeleteAsync(ct);
    }

    public async Task<int> DeleteAllSubmissionsAsync(Guid formId, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await db.Submissions.Where(s => s.FormId == formId).ExecuteDeleteAsync(ct);
    }

    /// <summary>Validates what a visitor sent against the form definition and stores it.</summary>
    public async Task<SubmitResult> SubmitAsync(string alias, IReadOnlyDictionary<string, string[]> values, string? pageUrl, CancellationToken ct = default)
    {
        var form = await GetByAliasAsync(alias, ct);
        if (form is null) return SubmitResult.Refused("This form does not exist.", 404);
        if (!form.IsEnabled) return SubmitResult.Refused("This form is closed.", 403);

        // Honeypot: report success without storing anything, so a bot learns nothing.
        if (values.TryGetValue("_hp", out var trap) && trap.Any(v => !string.IsNullOrWhiteSpace(v))) return SubmitResult.Success();

        var errors = new Dictionary<string, string>();
        var data = new Dictionary<string, string>();
        foreach (var f in form.GetFields())
        {
            values.TryGetValue(f.Name, out var raw);
            var given = (raw ?? []).Select(v => (v ?? string.Empty).Trim()).Where(v => v.Length > 0).ToArray();

            if (f.Type == FieldType.Checkbox)
            {
                var ticked = given.Length > 0;
                if (f.Required && !ticked) errors[f.Name] = "This box must be ticked.";
                data[f.Name] = ticked ? "Yes" : "No";
                continue;
            }

            if (given.Length == 0)
            {
                if (f.Required) errors[f.Name] = "This field is required.";
                data[f.Name] = string.Empty;
                continue;
            }

            var error = Validate(f, given);
            if (error is not null) errors[f.Name] = error;
            data[f.Name] = string.Join(", ", given);
        }
        if (errors.Count > 0) return SubmitResult.Invalid(errors);

        await using var db = await factory.CreateDbContextAsync(ct);
        db.Submissions.Add(new FormSubmission
        {
            FormId = form.Id,
            PageUrl = pageUrl is { Length: > 500 } ? pageUrl[..500] : pageUrl,
            DataJson = JsonSerializer.Serialize(data)
        });
        await db.SaveChangesAsync(ct);
        logger.LogInformation("Form {Alias}: submission stored", form.Alias);
        return SubmitResult.Success();
    }

    private static string? Validate(FormField f, string[] given)
    {
        var value = given[0];
        switch (f.Type)
        {
            case FieldType.Select:
            case FieldType.Radio:
                return given.Length == 1 && f.Options.Contains(value) ? null : "Choose one of the options.";
            case FieldType.CheckboxGroup:
                return given.All(f.Options.Contains) ? null : "Choose from the options offered.";
        }

        if (given.Length > 1) return "Send one value only.";
        if (value.Length > FormHtml.MaxLengthOf(f) && f.Type is not (FieldType.Number or FieldType.Date)) return $"Use at most {FormHtml.MaxLengthOf(f)} characters.";

        return f.Type switch
        {
            FieldType.Email => MailAddress.TryCreate(value, out var m) && m.Address == value && value.Contains('.') ? null : "Enter a valid email address.",
            FieldType.Number => decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out _) ? null : "Enter a number.",
            FieldType.Date => DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _) ? null : "Enter a valid date.",
            FieldType.Phone => value.All(c => char.IsDigit(c) || " +-()./".Contains(c)) && value.Count(char.IsDigit) >= 5 ? null : "Enter a valid phone number.",
            _ => null
        };
    }

    // ---- export ---------------------------------------------------------------------------------------

    public async Task<string?> ExportCsvAsync(Guid formId, CancellationToken ct = default)
    {
        var form = await GetAsync(formId, ct);
        if (form is null) return null;
        var fields = form.GetFields();
        var rows = await GetSubmissionsAsync(formId, ct);

        var sb = new StringBuilder('﻿'); // BOM: Excel reads the file as UTF-8
        sb.AppendJoin(',', new[] { "Received (UTC)", "Page" }.Concat(fields.Select(f => f.Label)).Select(Csv)).Append("\r\n");
        foreach (var row in rows.OrderBy(r => r.CreatedAt))
        {
            var data = row.GetData();
            var cells = new[] { row.CreatedAt.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture), row.PageUrl ?? string.Empty }
                .Concat(fields.Select(f => data.GetValueOrDefault(f.Name) ?? string.Empty));
            sb.AppendJoin(',', cells.Select(Csv)).Append("\r\n");
        }
        return sb.ToString();
    }

    // Quote every cell, and defuse spreadsheet formulas (=, +, -, @ at the start of a cell run as code in Excel).
    private static string Csv(string cell)
    {
        if (cell.Length > 0 && cell[0] is '=' or '+' or '-' or '@' or '\t' or '\r') cell = "'" + cell;
        return '"' + cell.Replace("\"", "\"\"", StringComparison.Ordinal) + '"';
    }
}

/// <summary>Limits how often one visitor can submit. A singleton holding a sliding window per client address.</summary>
public sealed class SubmissionThrottle
{
    private readonly ConcurrentDictionary<string, Queue<DateTime>> _hits = new();

    public bool TryAcquire(string client, int perMinute)
    {
        var now = DateTime.UtcNow;
        var queue = _hits.GetOrAdd(client, _ => new Queue<DateTime>());
        lock (queue)
        {
            while (queue.Count > 0 && now - queue.Peek() > TimeSpan.FromMinutes(1)) queue.Dequeue();
            if (queue.Count >= Math.Max(1, perMinute)) return false;
            queue.Enqueue(now);
        }

        if (_hits.Count > 5000)
        {
            foreach (var key in _hits.Keys.ToList())
                if (_hits.TryGetValue(key, out var q) && (q.Count == 0 || now - q.Peek() > TimeSpan.FromMinutes(1))) _hits.TryRemove(key, out _);
        }
        return true;
    }
}
