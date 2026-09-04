using System.ComponentModel.DataAnnotations;

namespace FitTrack.Models;

public sealed class ExerciseFormModel
{
    [Required(ErrorMessage = "Bitte einen Namen eingeben.")]
    [StringLength(100, ErrorMessage = "Der Name darf höchstens 100 Zeichen enthalten.")]
    public string Name { get; set; } = string.Empty;

    [Required(ErrorMessage = "Bitte eine Beschreibung eingeben.")]
    [StringLength(500, ErrorMessage = "Die Beschreibung darf höchstens 500 Zeichen enthalten.")]
    public string Description { get; set; } = string.Empty;

    [Required(ErrorMessage = "Bitte mindestens eine Muskelgruppe eingeben.")]
    public string MuscleGroups { get; set; } = string.Empty;

    [Required]
    public string ExerciseType { get; set; } = ExerciseTypes.Strength;

    [Required(ErrorMessage = "Bitte eine Kategorie eingeben.")]
    [StringLength(80, ErrorMessage = "Die Kategorie darf höchstens 80 Zeichen enthalten.")]
    public string Category { get; set; } = string.Empty;

    public string? ImageUrl { get; set; }
}