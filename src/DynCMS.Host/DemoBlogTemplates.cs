namespace DynCMS.Host;

/// <summary>
/// The stored Liquid templates of the demo blog (<see cref="DemoBlogSeeder"/>), besides the ones the starter site
/// takes from <c>TemplateSamples</c>. All of them use the classes of <c>dyncms-site.css</c> and are editable in
/// Settings → Templates once seeded; they are kept here only so the seeder has something to write.
/// </summary>
internal static class DemoBlogTemplates
{
    /// <summary>
    /// The demo home page: a hero with an image and a link to the blog, the featured articles, the three newest
    /// ones, the body text and the top-level pages as cards.
    /// </summary>
    public const string Home = """
        {%- comment -%}
          Demo home page. "content" is the home page; edit its properties under Content, this markup under
          Settings → Templates.
          'article' | content_of_type     every published article, wherever it is in the tree
          | sort: 'publishDate' | reverse newest first (the date picker stores yyyy-MM-dd, which sorts as text)
          | where: 'featured', true       the ones whose "Featured" toggle is on
          | slice: 0, 3                   the first three
        {%- endcomment -%}
        {%- assign hero_image = content.heroImage | media %}
        {%- assign blog = content.children | where: 'contentType', 'blog' | first %}
        {%- assign articles = 'article' | content_of_type | sort: 'publishDate' | reverse %}
        {%- assign featured = articles | where: 'featured', true | slice: 0, 3 %}
        {%- assign latest = articles | slice: 0, 3 %}
        <section class="hero">
          <div class="site-container{% if hero_image %} hero-grid{% endif %}">
            <div>
              <span class="eyebrow">{{ content.name }}</span>
              <h1>{{ content.heroTitle | default: content.name }}</h1>
              {%- if content.heroText %}
              <p class="lead">{{ content.heroText }}</p>
              {%- endif %}
              <p>
                {%- if blog %}
                <a class="btn btn-primary" href="{{ blog.url }}">Read the blog</a>
                {%- endif %}
                <a class="btn" href="/admin">Open the back office</a>
              </p>
            </div>
            {%- if hero_image %}
            <img class="hero-image" src="{{ hero_image.url }}" alt="" />
            {%- endif %}
          </div>
        </section>

        <section class="site-container">
          {%- if featured.size > 0 %}
          <div class="section-head">
            <h2>Featured</h2>
            {%- if blog %}<a href="{{ blog.url }}">All articles ›</a>{%- endif %}
          </div>
          <div class="cards">
            {%- for item in featured %}
            {% render 'articleCard', content: item %}
            {%- endfor %}
          </div>
          {%- endif %}

          {%- if content.bodyText %}
          <div class="prose">
            {{ content.bodyText | raw }}
          </div>
          {%- endif %}

          {%- if latest.size > 0 %}
          <div class="section-head">
            <h2>Latest from the blog</h2>
            {%- if blog %}<a href="{{ blog.url }}">All articles ›</a>{%- endif %}
          </div>
          <div class="cards">
            {%- for item in latest %}
            {% render 'articleCard', content: item %}
            {%- endfor %}
          </div>
          {%- endif %}

          {%- if content.children.size > 0 %}
          <div class="section-head">
            <h2>Pages</h2>
          </div>
          <div class="cards">
            {%- for item in content.children %}
            {% render 'card', content: item %}
            {%- endfor %}
          </div>
          {%- endif %}
        </section>
        """;

    /// <summary>A card for one article: image, category, date, title and summary. {% render 'articleCard', content: item %}.</summary>
    public const string ArticleCard = """
        {%- comment -%}
          Partial view for one article. Inside a partial, "content" is whatever the caller passed in:
            {% render 'articleCard', content: item %}
        {%- endcomment -%}
        <a class="card" href="{{ content.url }}">
          {%- assign image = content.image | media %}
          {%- if image %}
          <img class="card-image" src="{{ image.url }}" alt="{{ content.name }}" loading="lazy" />
          {%- else %}
          <div class="card-image card-image-empty"></div>
          {%- endif %}
          <div class="card-body">
            <div class="article-meta">
              {%- if content.category %}
              <span class="tag tag-strong">{{ content.category }}</span>
              {%- endif %}
              {%- if content.publishDate %}
              <time datetime="{{ content.publishDate }}">{{ content.publishDate | date: "%d %B %Y" }}</time>
              {%- endif %}
            </div>
            <h3>{{ content.name }}</h3>
            {%- if content.summary %}
            <p>{{ content.summary }}</p>
            {%- endif %}
          </div>
        </a>
        """;

    /// <summary>
    /// The blog page: its articles newest first, filtered by <c>?category=</c> when present, and paged with the
    /// <c>paginate</c> filter. The page size is a property of the blog page, so an editor changes it in the back
    /// office; the pager keeps the rest of the query string, so paging through a category stays in that category.
    /// </summary>
    public const string Blog = """
        {%- comment -%}
          Blog listing (Settings → Templates → "Blog"). Paging in Liquid:
          content.pageSize                "Articles per page" on the blog page; | plus: 0 turns the stored text into a number
          request.query.category          ?category=Tutorial in the address; the list is filtered when it is set
          | paginate: page_size           one page of the list. The page number comes from ?page= in the address
                                          (request.query.page); out-of-range numbers are clamped.
          paged.items                     the items on this page
          paged.page, page_count, total   the numbers; first and last are the positions of the page's first and
                                          last item in the whole list ("Showing 7–12 of 14")
          paged.previous_url / next_url   links to the neighbouring pages, keeping the rest of the query string
          paged.pages                     one hash per page: number, url, is_current
          'key' | dictionary: 'fallback'  a label from Settings → Dictionary, or the fallback when there is none
        {%- endcomment -%}
        {%- assign page_size = content.pageSize | default: 6 | plus: 0 %}
        {%- assign all_articles = content.children | sort: 'publishDate' | reverse %}
        {%- assign category = request.query.category | default: '' %}
        {%- if category != '' %}
        {%- assign articles = all_articles | where: 'category', category %}
        {%- else %}
        {%- assign articles = all_articles %}
        {%- endif %}
        {%- assign categories = all_articles | map: 'category' | compact | uniq | sort %}
        {%- assign paged = articles | paginate: page_size %}
        {%- assign summary = 'paging.summary' | dictionary: 'Showing {from}–{to} of {total} articles' | replace: '{from}', paged.first | replace: '{to}', paged.last | replace: '{total}', paged.total %}
        {%- assign page_of = 'paging.pageOf' | dictionary: 'Page {page} of {pages}' | replace: '{page}', paged.page | replace: '{pages}', paged.page_count %}
        {%- assign previous_label = 'paging.previous' | dictionary: 'Previous' %}
        {%- assign next_label = 'paging.next' | dictionary: 'Next' %}
        <section class="site-container page blog">
          {%- if content.ancestors.size > 0 %}
          <nav class="breadcrumbs" aria-label="Breadcrumb">
            {%- for a in content.ancestors %}
            <a href="{{ a.url }}">{{ a.name }}</a><span>/</span>
            {%- endfor %}
            <span class="current">{{ content.name }}</span>
          </nav>
          {%- endif %}

          <header class="page-head">
            <h1>{{ content.name }}</h1>
            {%- if content.intro %}
            <p class="lead">{{ content.intro }}</p>
            {%- endif %}
          </header>

          {%- if categories.size > 0 %}
          <nav class="tags" aria-label="Categories" style="margin: 0 0 20px;">
            <a class="tag{% if category == '' %} tag-strong{% endif %}" href="{{ content.url }}">All</a>
            {%- for c in categories %}
            <a class="tag{% if c == category %} tag-strong{% endif %}" href="{{ content.url }}?category={{ c | url_encode }}">{{ c }}</a>
            {%- endfor %}
          </nav>
          {%- endif %}

          <div class="section-head">
            <h2>{% if category != '' %}{{ category }}{% else %}All articles{% endif %}</h2>
            {%- if paged.page_count > 1 %}<span class="muted">{{ page_of }}</span>{%- endif %}
          </div>

          {%- if paged.total == 0 %}
          <p class="muted">No articles here yet.</p>
          {%- else %}
          <div class="cards">
            {%- for item in paged.items %}
            {% render 'articleCard', content: item %}
            {%- endfor %}
          </div>
          {%- endif %}

          {%- if paged.page_count > 1 %}
          <p class="pager-summary">{{ summary }}</p>
          <nav class="pager" aria-label="Pages">
            {%- if paged.has_previous %}
            <a class="pager-prev" href="{{ paged.previous_url }}" rel="prev">‹ {{ previous_label }}</a>
            {%- else %}
            <span class="pager-prev disabled" aria-disabled="true">‹ {{ previous_label }}</span>
            {%- endif %}
            {%- for p in paged.pages %}
            <a href="{{ p.url }}"{% if p.is_current %} class="active" aria-current="page"{% endif %}>{{ p.number }}</a>
            {%- endfor %}
            {%- if paged.has_next %}
            <a class="pager-next" href="{{ paged.next_url }}" rel="next">{{ next_label }} ›</a>
            {%- else %}
            <span class="pager-next disabled" aria-disabled="true">{{ next_label }} ›</span>
            {%- endif %}
          </nav>
          {%- endif %}
        </section>
        """;

    /// <summary>One article: meta line, title, summary, image, body, tags and three more articles.</summary>
    public const string Article = """
        {%- comment -%}
          Article page (Settings → Templates → "Article").
          content.tags        the Tags editor stores a JSON list, which arrives here as a Liquid array
          content.parent      the blog page; its URL with ?category= filters the list
          content.siblings    the other articles in the blog, for the "More articles" cards
        {%- endcomment -%}
        {%- assign image = content.image | media %}
        {%- assign tags = content.tags %}
        {%- assign more = content.siblings | sort: 'publishDate' | reverse | slice: 0, 3 %}
        <article class="site-container page article">
          {%- if content.ancestors.size > 0 %}
          <nav class="breadcrumbs" aria-label="Breadcrumb">
            {%- for a in content.ancestors %}
            <a href="{{ a.url }}">{{ a.name }}</a><span>/</span>
            {%- endfor %}
            <span class="current">{{ content.name }}</span>
          </nav>
          {%- endif %}

          <header class="page-head">
            <div class="article-meta">
              {%- if content.category %}
              <a class="tag tag-strong" href="{{ content.parent.url }}?category={{ content.category | url_encode }}">{{ content.category }}</a>
              {%- endif %}
              {%- if content.publishDate %}
              <time datetime="{{ content.publishDate }}">{{ content.publishDate | date: "%d %B %Y" }}</time>
              {%- endif %}
              {%- if content.author %}
              <span>by {{ content.author }}</span>
              {%- endif %}
            </div>
            <h1>{{ content.name }}</h1>
            {%- if content.summary %}
            <p class="lead">{{ content.summary }}</p>
            {%- endif %}
          </header>

          {%- if image %}
          <img class="page-image" src="{{ image.url }}" alt="{{ content.name }}" />
          {%- endif %}

          <div class="prose">
            {{ content.bodyText | raw }}
          </div>

          {%- if tags.size > 0 %}
          <div class="tags">
            {%- for tag in tags %}
            <span class="tag">{{ tag }}</span>
            {%- endfor %}
          </div>
          {%- endif %}

          {%- if more.size > 0 %}
          <div class="section-head">
            <h2>More articles</h2>
            {%- if content.parent %}<a href="{{ content.parent.url }}">All articles ›</a>{%- endif %}
          </div>
          <div class="cards">
            {%- for item in more %}
            {% render 'articleCard', content: item %}
            {%- endfor %}
          </div>
          {%- endif %}
        </article>
        """;
}
