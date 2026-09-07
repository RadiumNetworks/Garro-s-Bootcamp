using FitTrack.Models;

namespace FitTrack.Services;

public sealed class WorkoutService(BrowserStorageService storage, FitTrackDatabaseService database, AuthService auth)
{
    private const string StorageKey = "fittrack.workouts.v1";
    private List<WorkoutSession>? workouts;
    private List<WorkoutSession>? templates;
    private CurrentUserState? currentUser;

    public async Task<IReadOnlyList<WorkoutSession>> GetAllAsync()
    {
        await EnsureLoadedAsync();
        return workouts!.OrderByDescending(item => item.PerformedAt).ToList();
    }

    public async Task AddAsync(WorkoutSession workout)
    {
        await EnsureLoadedAsync();
        workout.IsTemplate = false;
        NormalizeSetEntries(workout);
        if (UsesSql)
        {
            workout = await database.SaveServerWorkoutAsync(workout);
        }

        workouts!.RemoveAll(item => item.Id == workout.Id);
        workouts.Add(workout);
        await database.SaveWorkoutAsync(workout);
    }

    public async Task UpdateAsync(WorkoutSession workout)
    {
        await EnsureLoadedAsync();
        NormalizeSetEntries(workout);
        workout.IsTemplate = false;
        if (UsesSql)
        {
            workout = await database.SaveServerWorkoutAsync(workout);
        }

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
    }

    public async Task DeleteAsync(Guid id)
    {
        await EnsureLoadedAsync();
        if (UsesSql)
        {
            await database.DeleteServerWorkoutAsync(id);
        }

        workouts!.RemoveAll(item => item.Id == id);
        await database.DeleteWorkoutAsync(id);
    }

    public async Task<IReadOnlyList<WorkoutSession>> GetTemplatesAsync()
    {
        await EnsureLoadedAsync();
        return templates!.OrderBy(item => item.Name).ToList();
    }

    public async Task<WorkoutSession> SaveTemplateAsync(WorkoutSession template)
    {
        await EnsureLoadedAsync();
        template.IsTemplate = true;
        template.PerformedAt = DateTime.Today;
        NormalizeSetEntries(template);
        template = template.Visibility == WorkoutVisibility.Global
            ? await database.SaveGlobalWorkoutAsync(template)
            : UsesSql
                ? await database.SaveServerWorkoutAsync(template)
                : template;

        templates!.RemoveAll(item => item.Id == template.Id
            || (item.Visibility == template.Visibility && item.Name.Equals(template.Name, StringComparison.OrdinalIgnoreCase)));
        templates.Add(template);
        if (template.Visibility == WorkoutVisibility.Personal)
        {
            await database.SaveWorkoutAsync(template);
        }
        return template;
    }

    public async Task DeleteTemplateAsync(Guid id)
    {
        await EnsureLoadedAsync();
        var template = templates!.FirstOrDefault(item => item.Id == id);
        if (template is null || template.Visibility == WorkoutVisibility.Global)
        {
            return;
        }

        if (UsesSql)
        {
            await database.DeleteServerWorkoutAsync(id);
        }
        templates!.Remove(template);
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
        workouts = [];
        templates = [];
        await database.InitializeAsync();
        await database.ClearWorkoutsAsync();
        await storage.RemoveAsync(StorageKey);
    }

    private async Task EnsureLoadedAsync()
    {
        if (workouts is not null)
        {
            return;
        }

        await database.InitializeAsync();
        currentUser = await auth.GetCurrentUserAsync();
        var exercises = await database.GetExercisesAsync();

        if (UsesSql)
        {
            var localWorkouts = await database.GetWorkoutsAsync();
            var serverWorkouts = await database.GetServerWorkoutsAsync();
            if (serverWorkouts is not null)
            {
                if (!await database.IsServerWorkoutsMigratedAsync())
                {
                    foreach (var localWorkout in localWorkouts.Where(item =>
                        !IsLegacySample(item)
                        && item.Visibility == WorkoutVisibility.Personal
                        && (string.IsNullOrWhiteSpace(item.OwnerUserName)
                            || string.Equals(item.OwnerUserName, currentUser!.UserName, StringComparison.OrdinalIgnoreCase))))
                    {
                        localWorkout.OwnerUserName = currentUser!.UserName;
                        var migratedWorkout = await database.SaveServerWorkoutAsync(localWorkout);
                        serverWorkouts.RemoveAll(item => item.Id == migratedWorkout.Id);
                        serverWorkouts.Add(migratedWorkout);
                    }
                }

                var globals = await database.GetGlobalWorkoutsAsync();
                workouts = serverWorkouts.Where(item => !item.IsTemplate).ToList();
                templates = serverWorkouts.Where(item => item.IsTemplate).Concat(globals).ToList();
                foreach (var item in workouts.Concat(templates))
                {
                    NormalizeSetEntries(item);
                    EnrichExerciseImages(item, exercises);
                }
                await database.ReplaceWorkoutsAsync(serverWorkouts);
                await storage.RemoveAsync(StorageKey);
                return;
            }
        }

        if (!await database.IsWorkoutStoreInitializedAsync())
        {
            workouts = await storage.GetAsync<List<WorkoutSession>>(StorageKey) ?? [];
            workouts.RemoveAll(IsLegacySample);
            foreach (var workout in workouts)
            {
                NormalizeSetEntries(workout);
                EnrichExerciseImages(workout, exercises);
                await database.SaveWorkoutAsync(workout);
            }

            await database.MarkWorkoutStoreInitializedAsync();
            await storage.RemoveAsync(StorageKey);
            templates = workouts.Where(item => item.IsTemplate).ToList();
            workouts = workouts.Where(item => !item.IsTemplate).ToList();
            return;
        }

        var storedWorkouts = await database.GetWorkoutsAsync();
        workouts = storedWorkouts.Where(item => !item.IsTemplate).ToList();
        templates = storedWorkouts.Where(item => item.IsTemplate).ToList();
        foreach (var sample in workouts.Where(IsLegacySample).ToList())
        {
            workouts.Remove(sample);
            await database.DeleteWorkoutAsync(sample.Id);
        }

        workouts.ForEach(NormalizeSetEntries);
        workouts.ForEach(workout => EnrichExerciseImages(workout, exercises));
        templates.ForEach(NormalizeSetEntries);
        templates.ForEach(template => EnrichExerciseImages(template, exercises));
    }

    private bool UsesSql => currentUser?.AuthenticationMode == "Sql" && currentUser.IsAuthenticated;

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

    private static bool IsLegacySample(WorkoutSession workout) =>
        workout.Visibility == WorkoutVisibility.Personal
        && ((workout.Name == "Ganzkörper Kraft"
                && workout.DurationMinutes == 52
                && workout.Exercises.Count == 1
                && workout.Exercises[0].Name == "Kniebeugen")
            || (workout.Name == "Morgenlauf"
                && workout.DurationMinutes == 34
                && workout.Exercises.Count == 1
                && workout.Exercises[0].Name == "Laufen"));
}
