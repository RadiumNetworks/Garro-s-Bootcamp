using System.ComponentModel.DataAnnotations;

namespace FitTrack.Models;

public sealed class WorkoutFormModel
{
    [Required(ErrorMessage = "Bitte gib dem Training einen Namen.")]
    public string Name { get; set; } = string.Empty;

    public DateTime PerformedAt { get; set; } = DateTime.Today;

    [Range(1, 600, ErrorMessage = "Die Dauer muss zwischen 1 und 600 Minuten liegen.")]
    public int DurationMinutes { get; set; } = 45;

    public string Notes { get; set; } = string.Empty;
}
