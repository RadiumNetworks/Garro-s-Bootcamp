using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using FitTrack;
using FitTrack.Services;

var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<App>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");

builder.Services.AddScoped(sp => new HttpClient { BaseAddress = new Uri(builder.HostEnvironment.BaseAddress) });
builder.Services.AddScoped<BrowserStorageService>();
builder.Services.AddScoped<FitTrackDatabaseService>();
builder.Services.AddScoped<WorkoutService>();
builder.Services.AddScoped<SettingsService>();
builder.Services.AddScoped<AuthService>();

await builder.Build().RunAsync();
