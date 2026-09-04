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