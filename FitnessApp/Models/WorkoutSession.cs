using System.ComponentModel.DataAnnotations;

namespace FitTrack.Models;

public sealed class WorkoutSession
{
    public Guid Id { get; set; } = Guid.NewGuid();

    [Required]
    public string Name { get; set; } = string.Empty;

    public DateTime PerformedAt { get; set; } = DateTime.Today;

    public DateTime RecordedAt { get; set; } = DateTime.UtcNow;

    public string? OwnerUserName { get; set; }

    public string Visibility { get; set; } = WorkoutVisibility.Personal;

    [Range(1, 600)]
    public int DurationMinutes { get; set; } = 45;

    public List<ExerciseEntry> Exercises { get; set; } = [];

    public string Notes { get; set; } = string.Empty;
}

public static class WorkoutVisibility
{
    public const string Personal = "Personal";
    public const string Global = "Global";
}

public sealed class ExerciseEntry
{
    public Guid ExerciseId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string MuscleGroup { get; set; } = string.Empty;
    public List<string> MuscleGroups { get; set; } = [];
    public string? ImageUrl { get; set; }
    public string ExerciseType { get; set; } = ExerciseTypes.Strength;
    public int Sets { get; set; }
    public int Repetitions { get; set; }
    public decimal WeightKg { get; set; }
    public List<ExerciseSetEntry> SetEntries { get; set; } = [];
    public int DurationMinutes { get; set; }
    public decimal DistanceKm { get; set; }
    public int? ElevationMeters { get; set; }
    public int Difficulty { get; set; } = 5;
}

public sealed class ExerciseSetEntry
{
    public int Repetitions { get; set; } = 10;
    public decimal WeightKg { get; set; }
    public int Difficulty { get; set; } = 5;
}
