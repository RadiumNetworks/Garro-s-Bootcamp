namespace FitTrack.Models;

public sealed class ExerciseDefinition
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string MuscleGroup { get; set; } = string.Empty;
    public List<string> MuscleGroups { get; set; } = [];
    public string Description { get; set; } = string.Empty;
    public string ExerciseType { get; set; } = ExerciseTypes.Strength;
    public string Category { get; set; } = string.Empty;
    public string? ImageUrl { get; set; }
}

public static class ExerciseTypes
{
    public const string Strength = "Strength";
    public const string Endurance = "Endurance";
    public const string Other = "Other";
}