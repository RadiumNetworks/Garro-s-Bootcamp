using FitTrack.Models;
using Microsoft.JSInterop;
using System.Net.Http.Json;

namespace FitTrack.Services;

public sealed class FitTrackDatabaseService(IJSRuntime jsRuntime, HttpClient httpClient)
{
    private IReadOnlyList<ExerciseDefinition>? catalog;

    public async Task InitializeAsync()
    {
        catalog ??= await httpClient.GetFromJsonAsync<List<ExerciseDefinition>>("data/exercises.json") ?? [];
        foreach (var exercise in catalog)
        {
            exercise.MuscleGroups ??= [];
            if (exercise.MuscleGroups.Count == 0 && !string.IsNullOrWhiteSpace(exercise.MuscleGroup))
            {
                exercise.MuscleGroups.Add(exercise.MuscleGroup);
            }

            exercise.MuscleGroup = exercise.MuscleGroups.FirstOrDefault() ?? exercise.MuscleGroup;
        }

        await jsRuntime.InvokeVoidAsync("fitTrackDb.initialize", (object)catalog);
    }

    public async Task<IReadOnlyList<ExerciseDefinition>> GetExercisesAsync()
    {
        var exercises = await jsRuntime.InvokeAsync<List<ExerciseDefinition>>("fitTrackDb.getExercises");
        return exercises.OrderBy(item => item.Name).ToList();
    }

    public async Task<ExerciseDefinition> AddExerciseAsync(ExerciseDefinition exercise)
    {
        using var response = await httpClient.PostAsJsonAsync("api/exercises", exercise);
        if (!response.IsSuccessStatusCode)
        {
            var message = await response.Content.ReadAsStringAsync();
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(message)
                ? "Die Übung konnte nicht gespeichert werden."
                : message);
        }

        var savedExercise = await response.Content.ReadFromJsonAsync<ExerciseDefinition>()
            ?? throw new InvalidOperationException("Der Server hat keine gespeicherte Übung zurückgegeben.");
        await jsRuntime.InvokeVoidAsync("fitTrackDb.saveExercise", savedExercise);
        return savedExercise;
    }

    public async Task<List<WorkoutSession>> GetWorkoutsAsync() =>
        await jsRuntime.InvokeAsync<List<WorkoutSession>>("fitTrackDb.getWorkouts");

    public async Task<List<WorkoutSession>> GetGlobalWorkoutsAsync()
    {
        try
        {
            return await httpClient.GetFromJsonAsync<List<WorkoutSession>>("api/workouts/global") ?? [];
        }
        catch
        {
            return [];
        }
    }

    public ValueTask SaveWorkoutAsync(WorkoutSession workout) =>
        jsRuntime.InvokeVoidAsync("fitTrackDb.saveWorkout", workout);

    public async Task SaveGlobalWorkoutAsync(WorkoutSession workout)
    {
        using var response = await httpClient.PostAsJsonAsync("api/workouts/global", workout);
        if (!response.IsSuccessStatusCode)
        {
            var message = await response.Content.ReadAsStringAsync();
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(message)
                ? "Das globale Training konnte nicht gespeichert werden."
                : message.Trim('"'));
        }
    }

    public ValueTask DeleteWorkoutAsync(Guid id) =>
        jsRuntime.InvokeVoidAsync("fitTrackDb.deleteWorkout", id);

    public ValueTask ClearWorkoutsAsync() =>
        jsRuntime.InvokeVoidAsync("fitTrackDb.clearWorkouts");

    public ValueTask<bool> IsWorkoutStoreInitializedAsync() =>
        jsRuntime.InvokeAsync<bool>("fitTrackDb.isWorkoutStoreInitialized");

    public ValueTask MarkWorkoutStoreInitializedAsync() =>
        jsRuntime.InvokeVoidAsync("fitTrackDb.markWorkoutStoreInitialized");

}