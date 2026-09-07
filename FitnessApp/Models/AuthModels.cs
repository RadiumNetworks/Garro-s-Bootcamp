using System.ComponentModel.DataAnnotations;

namespace FitTrack.Models;

public sealed class CurrentUserState
{
    public string AuthenticationMode { get; set; } = "Open";
    public bool IsAuthenticated { get; set; }
    public string? UserName { get; set; }
    public string? DisplayName { get; set; }
    public string? Role { get; set; }
    public bool CanManageUsers { get; set; }
}

public sealed class UserAccount
{
    public Guid UserId { get; set; }
    public string UserName { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string Role { get; set; } = string.Empty;
    public bool IsDisabled { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? LastLoginAt { get; set; }
}

public sealed class LoginModel
{
    [Required(ErrorMessage = "Bitte Benutzernamen eingeben.")]
    public string UserName { get; set; } = string.Empty;

    [Required(ErrorMessage = "Bitte Passwort eingeben.")]
    public string Password { get; set; } = string.Empty;
}

public sealed class CreateUserModel
{
    [Required(ErrorMessage = "Bitte Benutzernamen eingeben.")]
    [StringLength(120, MinimumLength = 3, ErrorMessage = "Der Benutzername muss zwischen 3 und 120 Zeichen lang sein.")]
    public string UserName { get; set; } = string.Empty;

    [Required(ErrorMessage = "Bitte Passwort eingeben.")]
    [StringLength(200, MinimumLength = 10, ErrorMessage = "Das Passwort muss mindestens 10 Zeichen lang sein.")]
    public string Password { get; set; } = string.Empty;

    [Required]
    public string Role { get; set; } = "User";
}

public sealed record CreateUserRequest(string UserName, string Password, string Role);
public sealed record LoginRequest(string UserName, string Password);
public sealed record UpdateDisplayNameRequest(string DisplayName);
public sealed record UserProfile(int WeeklyGoal = 3, bool IsLeaderboardPublic = false);
public sealed record UpdateUserProfileRequest(int WeeklyGoal, bool IsLeaderboardPublic);
public sealed record WeeklyStats(DateTime WeekStart, int SessionCount, int WeeklyGoal, bool GoalReached, int CurrentStreak, int LongestStreak);
public sealed record LeaderboardCategory(string CategoryKey, string DisplayName, string Description, string Unit, bool IsEnabled, int SortOrder);
public sealed record LeaderboardEntry(int Position, string DisplayName, decimal Value);
public sealed class UserEvent
{
    public Guid EventId { get; set; }
    [Required(ErrorMessage = "Bitte gib einen Titel ein.")]
    [StringLength(160)]
    public string Title { get; set; } = string.Empty;
    public string EventType { get; set; } = "PersonalGoal";
    public DateTime EventDate { get; set; } = DateTime.Today.AddDays(30);
    [StringLength(1000)]
    public string Description { get; set; } = string.Empty;
    public bool IsCompleted { get; set; }
}