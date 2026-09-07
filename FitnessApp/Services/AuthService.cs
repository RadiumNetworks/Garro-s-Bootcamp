using System.Net.Http.Json;
using FitTrack.Models;

namespace FitTrack.Services;

public sealed class AuthService(HttpClient httpClient)
{
    public async Task<CurrentUserState> GetCurrentUserAsync() =>
        await httpClient.GetFromJsonAsync<CurrentUserState>("api/auth/me") ?? new();

    public async Task<CurrentUserState?> LoginAsync(LoginModel model)
    {
        using var response = await httpClient.PostAsJsonAsync("api/auth/login", new LoginRequest(model.UserName, model.Password));
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        return await response.Content.ReadFromJsonAsync<CurrentUserState>();
    }

    public Task LogoutAsync() => httpClient.PostAsync("api/auth/logout", null);

    public Task UpdateDisplayNameAsync(string displayName) =>
        httpClient.PutAsJsonAsync("api/auth/display-name", new UpdateDisplayNameRequest(displayName));

    public async Task<UserProfile> GetUserProfileAsync()
    {
        using var response = await httpClient.GetAsync("api/profile");
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException("Das Benutzerprofil konnte nicht geladen werden.");
        }

        return await response.Content.ReadFromJsonAsync<UserProfile>()
            ?? throw new InvalidOperationException("Der Server hat kein Benutzerprofil zurückgegeben.");
    }

    public async Task<UserProfile> UpdateUserProfileAsync(UserProfile profile)
    {
        using var response = await httpClient.PutAsJsonAsync("api/profile", new UpdateUserProfileRequest(profile.WeeklyGoal, profile.IsLeaderboardPublic));
        if (!response.IsSuccessStatusCode)
        {
            var message = await response.Content.ReadAsStringAsync();
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(message)
                ? "Das Benutzerprofil konnte nicht gespeichert werden."
                : message.Trim('"'));
        }

        return await response.Content.ReadFromJsonAsync<UserProfile>()
            ?? throw new InvalidOperationException("Der Server hat kein Benutzerprofil zurückgegeben.");
    }

    public async Task<WeeklyStats> GetWeeklyStatsAsync()
    {
        using var response = await httpClient.GetAsync("api/stats/weekly");
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException("Die Wochenstatistik konnte nicht geladen werden.");
        }

        return await response.Content.ReadFromJsonAsync<WeeklyStats>()
            ?? throw new InvalidOperationException("Der Server hat keine Wochenstatistik zurückgegeben.");
    }

    public async Task<IReadOnlyList<LeaderboardCategory>> GetLeaderboardCategoriesAsync(bool admin = false) =>
        await httpClient.GetFromJsonAsync<List<LeaderboardCategory>>(admin ? "api/admin/leaderboards" : "api/leaderboards") ?? [];

    public async Task<IReadOnlyList<LeaderboardEntry>> GetLeaderboardAsync(string categoryKey) =>
        await httpClient.GetFromJsonAsync<List<LeaderboardEntry>>($"api/leaderboards/{Uri.EscapeDataString(categoryKey)}") ?? [];

    public async Task<LeaderboardCategory> UpdateLeaderboardCategoryAsync(LeaderboardCategory category)
    {
        using var response = await httpClient.PutAsJsonAsync($"api/admin/leaderboards/{Uri.EscapeDataString(category.CategoryKey)}", category);
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException("Die Ranglistenkategorie konnte nicht gespeichert werden.");
        return await response.Content.ReadFromJsonAsync<LeaderboardCategory>() ?? throw new InvalidOperationException("Der Server hat keine Kategorie zurückgegeben.");
    }

    public async Task<IReadOnlyList<UserEvent>> GetEventsAsync() =>
        await httpClient.GetFromJsonAsync<List<UserEvent>>("api/events") ?? [];

    public async Task<UserEvent> SaveEventAsync(UserEvent item)
    {
        using var response = item.EventId == Guid.Empty
            ? await httpClient.PostAsJsonAsync("api/events", item)
            : await httpClient.PutAsJsonAsync($"api/events/{item.EventId}", item);
        if (!response.IsSuccessStatusCode)
        {
            var message = await response.Content.ReadAsStringAsync();
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(message) ? "Das Ziel konnte nicht gespeichert werden." : message.Trim('"'));
        }
        return await response.Content.ReadFromJsonAsync<UserEvent>() ?? throw new InvalidOperationException("Der Server hat kein Ziel zurückgegeben.");
    }

    public async Task DeleteEventAsync(Guid eventId)
    {
        using var response = await httpClient.DeleteAsync($"api/events/{eventId}");
        if (!response.IsSuccessStatusCode && response.StatusCode != System.Net.HttpStatusCode.NotFound) throw new InvalidOperationException("Das Ziel konnte nicht gelöscht werden.");
    }

    public async Task<IReadOnlyList<UserAccount>> GetUsersAsync() =>
        await httpClient.GetFromJsonAsync<List<UserAccount>>("api/users") ?? [];

    public async Task<UserAccount> CreateUserAsync(CreateUserModel model)
    {
        using var response = await httpClient.PostAsJsonAsync("api/users", new CreateUserRequest(model.UserName.Trim(), model.Password, model.Role));
        if (!response.IsSuccessStatusCode)
        {
            var message = await response.Content.ReadAsStringAsync();
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(message) ? "Der Benutzer konnte nicht angelegt werden." : message.Trim('"'));
        }

        return await response.Content.ReadFromJsonAsync<UserAccount>()
            ?? throw new InvalidOperationException("Der Server hat keinen Benutzer zurückgegeben.");
    }
}