using DynCMS.Host;

var builder = WebApplication.CreateBuilder(args);

// DynCMS.Host registers the Razor components, the CMS services (bound to the "DynCms" section of appsettings.json),
// the back office and the starter site. It returns the CMS builder, so templates and startup tasks chain on here:
//   builder.AddDynCmsHost(o => o.SeedStarterSite = false)
//       .AddTemplate<HomeTemplate>("home", "Home page")
//       .AddStartupTask<MySeeder>();
builder.AddDynCmsHost();

var app = builder.Build();

// The whole request pipeline and the Blazor shell (App, Routes, layout, catch-all content page, /admin, /setup).
app.UseDynCmsHost();

app.Run();
