namespace DynCMS.Core.Services;

/// <summary>What a brand-new database should start with. Chosen on the <c>/setup</c> page.</summary>
public enum StarterContent
{
    /// <summary>Only the site root: a published home page and the document types and templates needed to add pages.</summary>
    EmptySite,

    /// <summary>
    /// A small demo blog: a home page with featured posts, an <em>About</em> page and a <em>Blog</em> section whose
    /// articles run to several pages, with categories, tags, images and a draft, to show what the system does.
    /// </summary>
    DemoBlog
}

/// <summary>
/// What the person completing setup asked the startup tasks to do. Registered as a singleton; the setup page
/// (and the Data tab when it switches databases) fills it in right before the runtime initialises the new
/// database, and clears it afterwards, so a seeder can tell "the setup page just ran, and this is what was
/// picked" from an ordinary start on an existing configuration file.
/// </summary>
public sealed class StartupContext
{
    /// <summary>
    /// The starting content chosen on <c>/setup</c>, or <c>null</c> when the application started from an existing
    /// configuration file and nobody was asked. Seeders fall back to their own default in that case.
    /// </summary>
    public StarterContent? RequestedStarterContent { get; internal set; }
}
