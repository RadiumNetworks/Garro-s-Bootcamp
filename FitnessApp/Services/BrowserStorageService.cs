using System.Text.Json;
using Microsoft.JSInterop;

namespace FitTrack.Services;

public sealed class BrowserStorageService(IJSRuntime jsRuntime)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<T?> GetAsync<T>(string key)
    {
        var json = await jsRuntime.InvokeAsync<string?>("localStorage.getItem", key);
        return string.IsNullOrWhiteSpace(json) ? default : JsonSerializer.Deserialize<T>(json, JsonOptions);
    }

    public ValueTask SetAsync<T>(string key, T value) =>
        jsRuntime.InvokeVoidAsync("localStorage.setItem", key, JsonSerializer.Serialize(value, JsonOptions));

    public ValueTask RemoveAsync(string key) =>
        jsRuntime.InvokeVoidAsync("localStorage.removeItem", key);
}
