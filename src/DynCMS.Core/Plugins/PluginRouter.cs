using System.ComponentModel;
using System.Reflection;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Template;
using Microsoft.Extensions.Logging;

namespace DynCMS.Core.Plugins;

/// <summary>A routable component a plugin brings: its <c>@page</c> template and where it renders.</summary>
/// <param name="PluginId">The plugin.</param>
/// <param name="ComponentType">The Blazor component.</param>
/// <param name="Template">The route template exactly as declared (<c>/admin/shop/orders/{id:guid}</c>).</param>
/// <param name="IsAdmin">True for templates under <c>/admin/</c>: rendered inside the back office for signed-in users.</param>
/// <param name="Layout">The component's <c>@layout</c>, if any; nested inside the host layout.</param>
public sealed record PluginPage(string PluginId, Type ComponentType, string Template, bool IsAdmin, Type? Layout);

/// <summary>The result of matching a request path against the plugin pages.</summary>
public sealed record PluginPageMatch(PluginPage Page, IReadOnlyDictionary<string, object?> RouteValues)
{
    /// <summary>
    /// The route values converted to the component's <c>[Parameter]</c> property types, keyed by property name, ready
    /// for <c>DynamicComponent</c>. Values without a matching parameter are dropped (passing them would throw).
    /// </summary>
    public Dictionary<string, object?> ComponentParameters()
    {
        var result = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var property in Page.ComponentType.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (property.GetCustomAttribute<ParameterAttribute>() is null || !property.CanWrite) continue;
            var key = RouteValues.Keys.FirstOrDefault(k => string.Equals(k, property.Name, StringComparison.OrdinalIgnoreCase));
            if (key is null) continue;
            var raw = RouteValues[key];
            result[property.Name] = Convert(raw, property.PropertyType);
        }
        return result;
    }

    private static object? Convert(object? value, Type target)
    {
        if (value is null) return null;
        if (target.IsInstanceOfType(value)) return value;
        var text = value as string ?? value.ToString();
        if (text is null) return null;
        var underlying = Nullable.GetUnderlyingType(target) ?? target;
        if (underlying == typeof(string)) return text;
        try
        {
            var converter = TypeDescriptor.GetConverter(underlying);
            return converter.CanConvertFrom(typeof(string)) ? converter.ConvertFromInvariantString(text) : System.Convert.ChangeType(text, underlying, System.Globalization.CultureInfo.InvariantCulture);
        }
        catch (Exception)
        {
            return null;
        }
    }
}

/// <summary>Matches request paths against the pages of the running plugins.</summary>
public interface IPluginRouter
{
    /// <summary>All pages of running plugins.</summary>
    IReadOnlyList<PluginPage> Pages { get; }

    /// <summary>
    /// Finds the plugin page for <paramref name="path"/> (absolute, no query string). <paramref name="admin"/> selects
    /// the area: back-office pages (<c>/admin/…</c>) or site pages. Returns null when no plugin page matches.
    /// </summary>
    PluginPageMatch? Match(string path, bool admin);

    /// <summary>Raised when the set of pages changes (a plugin started or stopped). Components re-evaluate their match on it.</summary>
    event Action? Changed;
}

internal sealed class PluginRouter(IInlineConstraintResolver constraints, ILogger<PluginRouter> logger) : IPluginRouter
{
    private sealed record Entry(PluginPage Page, TemplateMatcher Matcher, IReadOnlyList<(string Key, IRouteConstraint Constraint)> Constraints, decimal Precedence);

    private volatile Entry[] _entries = [];

    public IReadOnlyList<PluginPage> Pages => _entries.Select(e => e.Page).ToList();

    public event Action? Changed;

    public PluginPageMatch? Match(string path, bool admin)
    {
        var entries = _entries;
        if (entries.Length == 0) return null;

        if (string.IsNullOrEmpty(path)) path = "/";
        if (path[0] != '/') path = "/" + path;
        var pathString = new PathString(path);

        foreach (var entry in entries)
        {
            if (entry.Page.IsAdmin != admin) continue;
            var values = new RouteValueDictionary();
            if (!entry.Matcher.TryMatch(pathString, values)) continue;

            var ok = true;
            foreach (var (key, constraint) in entry.Constraints)
            {
                if (!constraint.Match(null, null, key, values, RouteDirection.IncomingRequest))
                {
                    ok = false;
                    break;
                }
            }
            if (!ok) continue;

            return new PluginPageMatch(entry.Page, new Dictionary<string, object?>(values, StringComparer.OrdinalIgnoreCase));
        }
        return null;
    }

    /// <summary>Discovers the routable components of an assembly. Called by the plugin manager when a plugin starts.</summary>
    public static IReadOnlyList<PluginPage> Discover(string pluginId, Assembly assembly, ILogger logger)
    {
        var pages = new List<PluginPage>();
        Type[] types;
        try { types = assembly.GetTypes(); }
        catch (ReflectionTypeLoadException ex) { types = ex.Types.Where(t => t is not null).ToArray()!; }

        foreach (var type in types)
        {
            if (type.IsAbstract || !typeof(Microsoft.AspNetCore.Components.IComponent).IsAssignableFrom(type)) continue;
            var routes = type.GetCustomAttributes<RouteAttribute>(inherit: false).ToList();
            if (routes.Count == 0) continue;
            var layout = type.GetCustomAttribute<LayoutAttribute>(inherit: true)?.LayoutType;
            foreach (var route in routes)
            {
                var template = route.Template.Trim();
                if (!template.StartsWith('/')) template = "/" + template;
                var isAdmin = template.Equals("/admin", StringComparison.OrdinalIgnoreCase) || template.StartsWith("/admin/", StringComparison.OrdinalIgnoreCase);
                pages.Add(new PluginPage(pluginId, type, template, isAdmin, layout));
            }
        }
        logger.LogDebug("Plugin {Plugin}: {Count} routable component(s)", pluginId, pages.Count);
        return pages;
    }

    /// <summary>Replaces the route table with the pages of the given plugins (most specific template first).</summary>
    public void Rebuild(IEnumerable<PluginPage> pages)
    {
        var entries = new List<Entry>();
        foreach (var page in pages)
        {
            try
            {
                var template = TemplateParser.Parse(page.Template.TrimStart('/'));
                var defaults = new RouteValueDictionary();
                var constraintList = new List<(string, IRouteConstraint)>();
                foreach (var parameter in template.Parameters)
                {
                    if (parameter.Name is null) continue;
                    if (parameter.DefaultValue is not null) defaults[parameter.Name] = parameter.DefaultValue;
                    foreach (var inline in parameter.InlineConstraints)
                    {
                        try
                        {
                            var constraint = constraints.ResolveConstraint(inline.Constraint);
                            if (constraint is not null) constraintList.Add((parameter.Name, constraint));
                            else logger.LogWarning("Plugin {Plugin}: unknown route constraint '{Constraint}' in {Template}; it is ignored", page.PluginId, inline.Constraint, page.Template);
                        }
                        catch (Exception ex)
                        {
                            logger.LogWarning(ex, "Plugin {Plugin}: route constraint '{Constraint}' in {Template} could not be resolved; it is ignored", page.PluginId, inline.Constraint, page.Template);
                        }
                    }
                }
                entries.Add(new Entry(page, new TemplateMatcher(template, defaults), constraintList, RoutePrecedence.ComputeInbound(template)));
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Plugin {Plugin}: route template {Template} on {Component} is invalid and was skipped", page.PluginId, page.Template, page.ComponentType.Name);
            }
        }

        _entries = entries.OrderByDescending(e => e.Precedence).ThenBy(e => e.Page.Template, StringComparer.Ordinal).ToArray();
        Changed?.Invoke();
    }
}
