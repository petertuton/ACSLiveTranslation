using KioskClient.Components;
using KioskClient.Services;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

var backendBaseUrl = builder.Configuration["BackendApi:BaseUrl"] ?? "http://localhost:7071";

builder.Services.AddHttpClient<BackendApiClient>(client =>
{
    client.BaseAddress = new Uri(backendBaseUrl);
});

var mediaProcessorBaseUrl = builder.Configuration["MediaProcessor:BaseUrl"] ?? "http://localhost:5001";

builder.Services.AddHttpClient<MediaProcessorClient>(client =>
{
    client.BaseAddress = new Uri(mediaProcessorBaseUrl);
});

var app = builder.Build();

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
    app.UseHsts();
}

app.UseHttpsRedirection();


app.UseAntiforgery();

app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();
