using FitTrack.Models;
using Microsoft.JSInterop;
using System.Net.Http.Json;

namespace FitTrack.Services;

public sealed class FitTrackDatabaseService(IJSRuntime jsRuntime, HttpClient httpClient)
{
    private IReadOnlyList<ExerciseDefinition>? catalog;

    public async Task InitializeAsync()
    {
        if (catalog is null)
        {
            try
            {
                catalog = await httpClient.GetFromJsonAsync<List<ExerciseDefinition>>("api/exercises") ?? [];
            }
            catch
            {
                catalog = await httpClient.GetFromJsonAsync<List<ExerciseDefinition>>("data/exercises.json") ?? [];
            }
        }

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

    public async Task<List<WorkoutSession>?> GetServerWorkoutsAsync()
    {
        try
        {
            using var response = await httpClient.GetAsync("api/workouts");
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            return await response.Content.ReadFromJsonAsync<List<WorkoutSession>>() ?? [];
        }
        catch
        {
            return null;
        }
    }

    public ValueTask SaveWorkoutAsync(WorkoutSession workout) =>
        jsRuntime.InvokeVoidAsync("fitTrackDb.saveWorkout", workout);

    public async Task<WorkoutSession> SaveServerWorkoutAsync(WorkoutSession workout)
    {
        using var response = await httpClient.PostAsJsonAsync("api/workouts", workout);
        if (!response.IsSuccessStatusCode)
        {
            var message = await response.Content.ReadAsStringAsync();
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(message)
                ? "Das Training konnte nicht in SQL gespeichert werden."
                : message.Trim('"'));
        }

        return await response.Content.ReadFromJsonAsync<WorkoutSession>()
            ?? throw new InvalidOperationException("Der Server hat kein gespeichertes Training zurückgegeben.");
    }

    public async Task<WorkoutSession> SaveGlobalWorkoutAsync(WorkoutSession workout)
    {
        using var response = await httpClient.PostAsJsonAsync("api/workouts/global", workout);
        if (!response.IsSuccessStatusCode)
        {
            var message = await response.Content.ReadAsStringAsync();
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(message)
                ? "Das globale Training konnte nicht gespeichert werden."
                : message.Trim('"'));
        }

        return await response.Content.ReadFromJsonAsync<WorkoutSession>()
            ?? throw new InvalidOperationException("Der Server hat keine gespeicherte Vorlage zurückgegeben.");
    }

    public ValueTask DeleteWorkoutAsync(Guid id) =>
        jsRuntime.InvokeVoidAsync("fitTrackDb.deleteWorkout", id);

    public async Task DeleteServerWorkoutAsync(Guid id)
    {
        using var response = await httpClient.DeleteAsync($"api/workouts/{id}");
        if (!response.IsSuccessStatusCode && response.StatusCode != System.Net.HttpStatusCode.NotFound)
        {
            throw new InvalidOperationException("Das Training konnte nicht aus SQL gelöscht werden.");
        }
    }

    public ValueTask ReplaceWorkoutsAsync(IReadOnlyList<WorkoutSession> workouts) =>
        jsRuntime.InvokeVoidAsync("fitTrackDb.replaceWorkouts", (object)workouts);

    public ValueTask<bool> IsServerWorkoutsMigratedAsync() =>
        jsRuntime.InvokeAsync<bool>("fitTrackDb.isServerWorkoutsMigrated");

    public ValueTask ClearWorkoutsAsync() =>
        jsRuntime.InvokeVoidAsync("fitTrackDb.clearWorkouts");

    public ValueTask<bool> IsWorkoutStoreInitializedAsync() =>
        jsRuntime.InvokeAsync<bool>("fitTrackDb.isWorkoutStoreInitialized");

    public ValueTask MarkWorkoutStoreInitializedAsync() =>
        jsRuntime.InvokeVoidAsync("fitTrackDb.markWorkoutStoreInitialized");

    public ValueTask SaveTrainingDraftAsync(WorkoutDraftState draft) =>
        jsRuntime.InvokeVoidAsync("fitTrackDb.saveTrainingDraft", draft);

    public ValueTask<WorkoutDraftState?> GetTrainingDraftAsync() =>
        jsRuntime.InvokeAsync<WorkoutDraftState?>("fitTrackDb.getTrainingDraft");

    public ValueTask ClearTrainingDraftAsync() =>
        jsRuntime.InvokeVoidAsync("fitTrackDb.clearTrainingDraft");

}