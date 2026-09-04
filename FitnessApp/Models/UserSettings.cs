using System.ComponentModel.DataAnnotations;

namespace FitTrack.Models;

public sealed class UserSettings
{
    [Required(ErrorMessage = "Bitte gib einen Namen ein.")]
    public string DisplayName { get; set; } = "Sportler";

    [Range(1, 14, ErrorMessage = "Das Wochenziel muss zwischen 1 und 14 liegen.")]
    public int WeeklyGoal { get; set; } = 3;

    public bool DarkMode { get; set; }
}
