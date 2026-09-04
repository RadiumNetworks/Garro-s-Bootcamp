using FitTrack.Models;

namespace FitTrack.Services;

public sealed class SettingsService(BrowserStorageService storage)
{
    private const string StorageKey = "fittrack.settings.v1";

    public async Task<UserSettings> GetAsync() =>
        await storage.GetAsync<UserSettings>(StorageKey) ?? new UserSettings();

    public Task SaveAsync(UserSettings settings) => storage.SetAsync(StorageKey, settings).AsTask();
}
