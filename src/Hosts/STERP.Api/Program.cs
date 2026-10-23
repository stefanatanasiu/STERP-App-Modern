using Sterp.Modules.Composition;
using Sterp.Platform.Hosting;

// STERP-API (FR-STERP-API-001..016): online engine path of the modernized STERP application.
var builder = WebApplication.CreateBuilder(args);
builder.AddSterpHost("online", SterpModules.All());

var app = builder.Build();
app.UseSterpHost("online");
app.Run();
