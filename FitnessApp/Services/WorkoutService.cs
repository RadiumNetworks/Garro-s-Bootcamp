using FitTrack.Models;

namespace FitTrack.Services;

public sealed class WorkoutService(BrowserStorageService storage, FitTrackDatabaseService database)
{
    private const string StorageKey = "fittrack.workouts.v1";
    private List<WorkoutSession>? workouts;

    public async Task<IReadOnlyList<WorkoutSession>> GetAllAsync()
    {
        await EnsureLoadedAsync();
        return workouts!.OrderByDescending(item => item.PerformedAt).ToList();
    }

    public async Task AddAsync(WorkoutSession workout)
    {
        await EnsureLoadedAsync();
        NormalizeSetEntries(workout);
        workouts!.Add(workout);
        await database.SaveWorkoutAsync(workout);
        if (workout.Visibility == WorkoutVisibility.Global)
        {
            await database.SaveGlobalWorkoutAsync(workout);
        }
    }

    public async Task UpdateAsync(WorkoutSession workout)
    {
        await EnsureLoadedAsync();
        NormalizeSetEntries(workout);
        var index = workouts!.FindIndex(item => item.Id == workout.Id);
        if (index >= 0)
        {
            workouts[index] = workout;
        }
        else
        {
            workouts.Add(workout);
        }

        await database.SaveWorkoutAsync(workout);
        if (workout.Visibility == WorkoutVisibility.Global)
        {
            await database.SaveGlobalWorkoutAsync(workout);
        }
    }

    public async Task DeleteAsync(Guid id)
    {
        await EnsureLoadedAsync();
        workouts!.RemoveAll(item => item.Id == id);
        await database.DeleteWorkoutAsync(id);
    }

    public async Task<ExerciseEntry?> GetLastExerciseAsync(Guid exerciseId)
    {
        await EnsureLoadedAsync();
        return workouts!
            .OrderByDescending(item => item.PerformedAt)
            .ThenByDescending(item => item.RecordedAt)
            .SelectMany(item => item.Exercises)
            .FirstOrDefault(item => item.ExerciseId == exerciseId);
    }

    public async Task ResetAsync()
    {
        workouts = CreateSamples();
        await database.InitializeAsync();
        await database.ClearWorkoutsAsync();
        foreach (var workout in workouts)
        {
            NormalizeSetEntries(workout);
            await database.SaveWorkoutAsync(workout);
        }
    }

    private async Task EnsureLoadedAsync()
    {
        if (workouts is not null)
        {
            return;
        }

        await database.InitializeAsync();
        var exercises = await database.GetExercisesAsync();
        if (!await database.IsWorkoutStoreInitializedAsync())
        {
            workouts = await storage.GetAsync<List<WorkoutSession>>(StorageKey) ?? CreateSamples();
            foreach (var workout in workouts)
            {
                NormalizeSetEntries(workout);
                EnrichExerciseImages(workout, exercises);
                await database.SaveWorkoutAsync(workout);
            }

            await database.MarkWorkoutStoreInitializedAsync();
            return;
        }

        workouts = await database.GetWorkoutsAsync();
        var globalWorkouts = await database.GetGlobalWorkoutsAsync();
        foreach (var localGlobalWorkout in workouts.Where(item => item.Visibility == WorkoutVisibility.Global))
        {
            try
            {
                await database.SaveGlobalWorkoutAsync(localGlobalWorkout);
            }
            catch
            {
                // Non-admin users may have local global templates but are not allowed to publish them.
            }
        }

        foreach (var globalWorkout in globalWorkouts.Where(globalWorkout => workouts.All(item => item.Id != globalWorkout.Id)))
        {
            workouts.Add(globalWorkout);
        }

        workouts.ForEach(NormalizeSetEntries);
        workouts.ForEach(workout => EnrichExerciseImages(workout, exercises));
    }

    private static void EnrichExerciseImages(WorkoutSession workout, IReadOnlyList<ExerciseDefinition> exercises)
    {
        foreach (var exercise in workout.Exercises.Where(item => string.IsNullOrWhiteSpace(item.ImageUrl)))
        {
            exercise.ImageUrl = exercises.FirstOrDefault(item => item.Id == exercise.ExerciseId)?.ImageUrl;
        }
    }

    private static void NormalizeSetEntries(WorkoutSession workout)
    {
        foreach (var exercise in workout.Exercises)
        {
            exercise.MuscleGroups ??= [];
            if (exercise.MuscleGroups.Count == 0 && !string.IsNullOrWhiteSpace(exercise.MuscleGroup))
            {
                exercise.MuscleGroups.Add(exercise.MuscleGroup);
            }

            if (exercise.ExerciseId == Guid.Empty && exercise.Name.Equals("Laufen", StringComparison.OrdinalIgnoreCase))
            {
                exercise.ExerciseId = Guid.Parse("10000000-4000-2000-7000-000000000001");
                exercise.ExerciseType = ExerciseTypes.Endurance;
                exercise.DurationMinutes = exercise.DurationMinutes > 0 ? exercise.DurationMinutes : workout.DurationMinutes;
                exercise.Difficulty = exercise.Difficulty > 0 ? exercise.Difficulty : 5;
                exercise.SetEntries = [];
            }

            if (exercise.ExerciseType == ExerciseTypes.Strength && exercise.SetEntries.Count == 0)
            {
                exercise.SetEntries = Enumerable.Range(0, Math.Max(1, exercise.Sets))
                    .Select(_ => new ExerciseSetEntry
                    {
                        Repetitions = Math.Max(1, exercise.Repetitions),
                        WeightKg = exercise.WeightKg,
                        Difficulty = 5
                    })
                    .ToList();
            }

            if (exercise.SetEntries.Count > 0)
            {
                exercise.Sets = exercise.SetEntries.Count;
                exercise.Repetitions = exercise.SetEntries[0].Repetitions;
                exercise.WeightKg = exercise.SetEntries[0].WeightKg;
            }
        }
    }

    private static List<WorkoutSession> CreateSamples() =>
    [
        new()
        {
            Name = "Ganzkörper Kraft",
            PerformedAt = DateTime.Today.AddDays(-1),
            DurationMinutes = 52,
            Exercises = [new() { ExerciseId = Guid.Parse("10000000-2000-1000-3000-000000000001"), Name = "Kniebeugen", MuscleGroup = "Beine", Sets = 4, Repetitions = 8, WeightKg = 60 }]
        },
        new()
        {
            Name = "Morgenlauf",
            PerformedAt = DateTime.Today.AddDays(-3),
            DurationMinutes = 34,
            Exercises = [new() { ExerciseId = Guid.Parse("10000000-4000-2000-7000-000000000001"), Name = "Laufen", MuscleGroup = "Ausdauer", ExerciseType = ExerciseTypes.Endurance, DurationMinutes = 34, DistanceKm = 5, Difficulty = 5 }]
        }
    ];
}
