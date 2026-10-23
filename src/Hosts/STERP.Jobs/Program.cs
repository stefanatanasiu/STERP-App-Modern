using Sterp.Modules.Composition;
using Sterp.Platform.Hosting;

// STERP-JOBS (FR-STERP-JOBS-001..013): batch engine path of the modernized STERP application. It never starts work on its own.
var builder = WebApplication.CreateBuilder(args);
builder.AddSterpHost("batch", SterpModules.All());

var app = builder.Build();
app.UseSterpHost("batch");
app.Run();
