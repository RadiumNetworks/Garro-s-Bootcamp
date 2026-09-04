using System.Text.Json;
using System.Text.Json.Serialization;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.FileProviders;
using Microsoft.Data.SqlClient;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.Cookie.Name = "FitTrack.Auth";
        options.Cookie.HttpOnly = true;
        options.Cookie.SameSite = SameSiteMode.Lax;
        options.LoginPath = "/benutzer";
        options.AccessDeniedPath = "/benutzer";
    });
builder.Services.AddAuthorization();
builder.Services.AddSingleton<PasswordHasher<AppUserPassword>>();

var app = builder.Build();

var catalogLock = new SemaphoreSlim(1, 1);
var jsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web)
{
    WriteIndented = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
};

app.UseBlazorFrameworkFiles();
app.UseStaticFiles();
app.UseAuthentication();
app.UseAuthorization();

var mediaPath = Path.GetFullPath(Path.Combine(app.Environment.ContentRootPath, "..", "Media"));
if (Directory.Exists(mediaPath))
{
    app.UseStaticFiles(new StaticFileOptions
    {
        FileProvider = new PhysicalFileProvider(mediaPath),
        RequestPath = "/media"
    });
}

app.MapPost("/api/exercises", async (ExerciseRequest request) =>
{
    var validationError = Validate(request);
    if (validationError is not null)
    {
        return Results.BadRequest(validationError);
    }

    var catalogPath = GetCatalogPath(app.Environment);
    await catalogLock.WaitAsync();
    try
    {
        var catalog = await ReadCatalogAsync(catalogPath, jsonOptions);
        if (catalog.Any(item => string.Equals(item.Name, request.Name.Trim(), StringComparison.OrdinalIgnoreCase)))
        {
            return Results.Conflict($"Eine Übung mit dem Namen „{request.Name.Trim()}“ ist bereits vorhanden.");
        }

        var exercise = new ExerciseRecord(
            request.Id == Guid.Empty ? Guid.NewGuid() : request.Id,
            request.Name.Trim(),
            request.Description.Trim(),
            request.MuscleGroups.Select(group => group.Trim()).Where(group => group.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            request.ExerciseType,
            request.Category.Trim(),
            string.IsNullOrWhiteSpace(request.ImageUrl) ? null : request.ImageUrl.Trim());

        catalog.Add(exercise);
        await WriteCatalogAsync(catalogPath, catalog, jsonOptions);
        return Results.Created($"/api/exercises/{exercise.Id}", exercise);
    }
    finally
    {
        catalogLock.Release();
    }
});

app.MapGet("/api/auth/me", (HttpContext context) =>
{
    var mode = GetAuthenticationMode(app.Configuration);
    if (mode == AuthenticationModes.Open)
    {
        return Results.Ok(new CurrentUserResponse(mode, true, "OpenSetup", "OpenSetup", "Admin", true));
    }

    var identity = context.User.Identity;
    var userName = identity?.IsAuthenticated == true ? context.User.Identity?.Name : null;
    var displayName = context.User.FindFirstValue("display_name") ?? userName;
    var role = context.User.FindFirstValue(ClaimTypes.Role);
    return Results.Ok(new CurrentUserResponse(mode, identity?.IsAuthenticated == true, userName, displayName, role, role == Roles.Admin));
});

app.MapPost("/api/auth/login", async (LoginRequest request, HttpContext context, PasswordHasher<AppUserPassword> hasher) =>
{
    if (GetAuthenticationMode(app.Configuration) != AuthenticationModes.Sql)
    {
        return Results.BadRequest("Login ist nur im SQL-Authentifizierungsmodus aktiv.");
    }

    var user = await FindUserByNameAsync(app.Configuration, request.UserName);
    if (user is null || user.IsDisabled)
    {
        return Results.Unauthorized();
    }

    var verification = hasher.VerifyHashedPassword(new AppUserPassword(user.UserId, user.UserName), user.PasswordHash, request.Password);
    if (verification == PasswordVerificationResult.Failed)
    {
        return Results.Unauthorized();
    }

    await TouchLastLoginAsync(app.Configuration, user.UserId);
    var claims = new[]
    {
        new Claim(ClaimTypes.NameIdentifier, user.UserId.ToString()),
        new Claim(ClaimTypes.Name, user.UserName),
        new Claim("display_name", user.DisplayName ?? user.UserName),
        new Claim(ClaimTypes.Role, user.Role)
    };
    await context.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, new ClaimsPrincipal(new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme)));
    return Results.Ok(new CurrentUserResponse(AuthenticationModes.Sql, true, user.UserName, user.DisplayName ?? user.UserName, user.Role, user.Role == Roles.Admin));
});

app.MapPost("/api/auth/logout", async (HttpContext context) =>
{
    await context.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
    return Results.Ok();
});

app.MapPut("/api/auth/display-name", async (UpdateDisplayNameRequest request, HttpContext context) =>
{
    if (GetAuthenticationMode(app.Configuration) != AuthenticationModes.Sql)
    {
        return Results.Ok();
    }

    if (context.User.Identity?.IsAuthenticated != true
        || !Guid.TryParse(context.User.FindFirstValue(ClaimTypes.NameIdentifier), out var userId))
    {
        return Results.Unauthorized();
    }

    if (string.IsNullOrWhiteSpace(request.DisplayName) || request.DisplayName.Trim().Length > 160)
    {
        return Results.BadRequest("Der Anzeigename ist erforderlich und darf höchstens 160 Zeichen enthalten.");
    }

    var displayName = request.DisplayName.Trim();
    await UpdateDisplayNameAsync(app.Configuration, userId, displayName);
    var claims = new[]
    {
        new Claim(ClaimTypes.NameIdentifier, userId.ToString()),
        new Claim(ClaimTypes.Name, context.User.Identity?.Name ?? string.Empty),
        new Claim("display_name", displayName),
        new Claim(ClaimTypes.Role, context.User.FindFirstValue(ClaimTypes.Role) ?? Roles.User)
    };
    await context.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, new ClaimsPrincipal(new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme)));
    return Results.Ok();
});

app.MapGet("/api/users", async (HttpContext context) =>
{
    var guard = RequireUserManagementAccess(app.Configuration, context);
    if (guard is not null)
    {
        return guard;
    }

    return Results.Ok(await GetUsersAsync(app.Configuration));
});

app.MapPost("/api/users", async (CreateUserRequest request, HttpContext context, PasswordHasher<AppUserPassword> hasher) =>
{
    var guard = RequireUserManagementAccess(app.Configuration, context);
    if (guard is not null)
    {
        return guard;
    }

    var validationError = ValidateUser(request);
    if (validationError is not null)
    {
        return Results.BadRequest(validationError);
    }

    var userId = Guid.NewGuid();
    var passwordHash = hasher.HashPassword(new AppUserPassword(userId, request.UserName.Trim()), request.Password);
    try
    {
        var user = await CreateUserAsync(app.Configuration, userId, request.UserName.Trim(), passwordHash, request.Role);
        return Results.Created($"/api/users/{user.UserId}", user);
    }
    catch (InvalidOperationException exception)
    {
        return Results.BadRequest(exception.Message);
    }
});

app.MapGet("/api/workouts/global", async (HttpContext context) =>
{
    if (GetAuthenticationMode(app.Configuration) == AuthenticationModes.Sql && context.User.Identity?.IsAuthenticated != true)
    {
        return Results.Unauthorized();
    }

    return Results.Ok(await GetGlobalWorkoutsAsync(app.Configuration));
});

app.MapPost("/api/workouts/global", async (WorkoutDto workout, HttpContext context) =>
{
    if (GetAuthenticationMode(app.Configuration) == AuthenticationModes.Sql && !context.User.IsInRole(Roles.Admin))
    {
        return Results.Forbid();
    }

    if (!Guid.TryParse(context.User.FindFirstValue(ClaimTypes.NameIdentifier), out var ownerUserId))
    {
        var admin = await FindFirstAdminAsync(app.Configuration);
        if (admin is null)
        {
            return Results.BadRequest("Für globale Trainings wird ein Admin-Benutzer benötigt.");
        }

        ownerUserId = admin.UserId;
    }

    await SaveGlobalWorkoutAsync(app.Configuration, workout, ownerUserId);
    return Results.Ok();
});

app.MapFallbackToFile("index.html");
app.Run();

static IResult? RequireUserManagementAccess(IConfiguration configuration, HttpContext context)
{
    if (GetAuthenticationMode(configuration) == AuthenticationModes.Open)
    {
        return null;
    }

    if (context.User.Identity?.IsAuthenticated != true)
    {
        return Results.Unauthorized();
    }

    return context.User.IsInRole(Roles.Admin) ? null : Results.Forbid();
}

static string GetAuthenticationMode(IConfiguration configuration)
{
    var mode = configuration["Authentication:Mode"];
    return string.Equals(mode, AuthenticationModes.Sql, StringComparison.OrdinalIgnoreCase)
        ? AuthenticationModes.Sql
        : AuthenticationModes.Open;
}

static string GetConnectionString(IConfiguration configuration) =>
    configuration.GetConnectionString("FitTrack")
    ?? throw new InvalidOperationException("ConnectionStrings:FitTrack ist nicht konfiguriert.");

static string? Validate(ExerciseRequest request)
{
    if (string.IsNullOrWhiteSpace(request.Name) || request.Name.Trim().Length > 100)
        return "Der Name ist erforderlich und darf höchstens 100 Zeichen enthalten.";
    if (string.IsNullOrWhiteSpace(request.Description) || request.Description.Trim().Length > 500)
        return "Die Beschreibung ist erforderlich und darf höchstens 500 Zeichen enthalten.";
    if (request.MuscleGroups is null || request.MuscleGroups.All(string.IsNullOrWhiteSpace))
        return "Mindestens eine Muskelgruppe ist erforderlich.";
    if (request.ExerciseType is not ("Strength" or "Endurance" or "Other"))
        return "Der Übungstyp ist ungültig.";
    if (string.IsNullOrWhiteSpace(request.Category) || request.Category.Trim().Length > 80)
        return "Die Kategorie ist erforderlich und darf höchstens 80 Zeichen enthalten.";
    if (!IsValidImagePath(request.ImageUrl))
        return "Die Bild-URL muss eine HTTP-/HTTPS-Adresse oder ein lokaler Pfad wie /media/datei.png sein.";
    return null;
}

static bool IsValidImagePath(string? imageUrl)
{
    if (string.IsNullOrWhiteSpace(imageUrl))
        return true;

    var trimmed = imageUrl.Trim();
    if (trimmed.StartsWith('/') && !trimmed.Contains("..", StringComparison.Ordinal))
        return true;

    return Uri.TryCreate(trimmed, UriKind.Absolute, out var imageUri)
        && imageUri.Scheme is "http" or "https";
}

static string GetCatalogPath(IWebHostEnvironment environment)
{
    var sourcePath = Path.GetFullPath(Path.Combine(environment.ContentRootPath, "..", "wwwroot", "data", "exercises.json"));
    if (File.Exists(sourcePath))
        return sourcePath;

    var publishedPath = Path.Combine(environment.WebRootPath, "data", "exercises.json");
    if (File.Exists(publishedPath))
        return publishedPath;

    throw new FileNotFoundException("Der Übungskatalog wurde nicht gefunden.", sourcePath);
}

static async Task<List<ExerciseRecord>> ReadCatalogAsync(string path, JsonSerializerOptions options)
{
    await using var stream = File.OpenRead(path);
    return await JsonSerializer.DeserializeAsync<List<ExerciseRecord>>(stream, options) ?? [];
}

static async Task WriteCatalogAsync(string path, List<ExerciseRecord> catalog, JsonSerializerOptions options)
{
    var temporaryPath = $"{path}.{Guid.NewGuid():N}.tmp";
    try
    {
        await using (var stream = File.Create(temporaryPath))
        {
            await JsonSerializer.SerializeAsync(stream, catalog, options);
            await stream.FlushAsync();
        }

        File.Move(temporaryPath, path, true);
    }
    finally
    {
        if (File.Exists(temporaryPath))
            File.Delete(temporaryPath);
    }
}

static string? ValidateUser(CreateUserRequest request)
{
    if (string.IsNullOrWhiteSpace(request.UserName) || request.UserName.Trim().Length is < 3 or > 120)
        return "Der Benutzername muss zwischen 3 und 120 Zeichen lang sein.";
    if (string.IsNullOrWhiteSpace(request.Password) || request.Password.Length < 10)
        return "Das Passwort muss mindestens 10 Zeichen lang sein.";
    if (request.Role is not (Roles.User or Roles.Admin))
        return "Die Rolle ist ungültig.";
    return null;
}

static async Task<IReadOnlyList<UserResponse>> GetUsersAsync(IConfiguration configuration)
{
    await using var connection = new SqlConnection(GetConnectionString(configuration));
    await connection.OpenAsync();
    await using var command = new SqlCommand("""
        SELECT UserId, UserName, DisplayName, Role, IsDisabled, CreatedAt, LastLoginAt
        FROM dbo.AppUser
        ORDER BY UserName;
        """, connection);
    await using var reader = await command.ExecuteReaderAsync();
    var users = new List<UserResponse>();
    while (await reader.ReadAsync())
    {
        users.Add(new UserResponse(
            reader.GetGuid(0),
            reader.GetString(1),
            reader.IsDBNull(2) ? reader.GetString(1) : reader.GetString(2),
            reader.GetString(3),
            reader.GetBoolean(4),
            reader.GetDateTime(5),
            reader.IsDBNull(6) ? null : reader.GetDateTime(6)));
    }

    return users;
}

static async Task<DatabaseUser?> FindUserByNameAsync(IConfiguration configuration, string userName)
{
    await using var connection = new SqlConnection(GetConnectionString(configuration));
    await connection.OpenAsync();
    await using var command = new SqlCommand("""
        SELECT UserId, UserName, DisplayName, PasswordHash, Role, IsDisabled
        FROM dbo.AppUser
        WHERE NormalizedUserName = UPPER(@UserName);
        """, connection);
    command.Parameters.AddWithValue("@UserName", userName.Trim());
    await using var reader = await command.ExecuteReaderAsync();
    return await reader.ReadAsync()
        ? new DatabaseUser(reader.GetGuid(0), reader.GetString(1), reader.IsDBNull(2) ? reader.GetString(1) : reader.GetString(2), reader.GetString(3), reader.GetString(4), reader.GetBoolean(5))
        : null;
}

static async Task<UserResponse> CreateUserAsync(IConfiguration configuration, Guid userId, string userName, string passwordHash, string role)
{
    await using var connection = new SqlConnection(GetConnectionString(configuration));
    await connection.OpenAsync();
    await using var command = new SqlCommand("""
        INSERT INTO dbo.AppUser (UserId, UserName, DisplayName, PasswordHash, Role)
        OUTPUT inserted.UserId, inserted.UserName, inserted.DisplayName, inserted.Role, inserted.IsDisabled, inserted.CreatedAt, inserted.LastLoginAt
        VALUES (@UserId, @UserName, @UserName, @PasswordHash, @Role);
        """, connection);
    command.Parameters.AddWithValue("@UserId", userId);
    command.Parameters.AddWithValue("@UserName", userName);
    command.Parameters.AddWithValue("@PasswordHash", passwordHash);
    command.Parameters.AddWithValue("@Role", role);

    try
    {
        await using var reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
            throw new InvalidOperationException("Der Benutzer konnte nicht angelegt werden.");

        return new UserResponse(reader.GetGuid(0), reader.GetString(1), reader.IsDBNull(2) ? reader.GetString(1) : reader.GetString(2), reader.GetString(3), reader.GetBoolean(4), reader.GetDateTime(5), reader.IsDBNull(6) ? null : reader.GetDateTime(6));
    }
    catch (SqlException exception) when (exception.Number is 2601 or 2627)
    {
        throw new InvalidOperationException("Dieser Benutzername ist bereits vergeben.", exception);
    }
}

static async Task<DatabaseUser?> FindFirstAdminAsync(IConfiguration configuration)
{
    await using var connection = new SqlConnection(GetConnectionString(configuration));
    await connection.OpenAsync();
    await using var command = new SqlCommand("""
        SELECT TOP 1 UserId, UserName, DisplayName, PasswordHash, Role, IsDisabled
        FROM dbo.AppUser
        WHERE Role = N'Admin' AND IsDisabled = 0
        ORDER BY CreatedAt;
        """, connection);
    await using var reader = await command.ExecuteReaderAsync();
    return await reader.ReadAsync()
        ? new DatabaseUser(reader.GetGuid(0), reader.GetString(1), reader.IsDBNull(2) ? reader.GetString(1) : reader.GetString(2), reader.GetString(3), reader.GetString(4), reader.GetBoolean(5))
        : null;
}

static async Task<IReadOnlyList<WorkoutDto>> GetGlobalWorkoutsAsync(IConfiguration configuration)
{
    await using var connection = new SqlConnection(GetConnectionString(configuration));
    await connection.OpenAsync();
    await using var workoutCommand = new SqlCommand("""
        SELECT WorkoutId, Name, PerformedAt, RecordedAt, DurationMinutes, Notes
        FROM dbo.Workout
        WHERE Visibility = N'Global'
        ORDER BY Name;
        """, connection);
    await using var workoutReader = await workoutCommand.ExecuteReaderAsync();
    var workouts = new List<WorkoutDto>();
    while (await workoutReader.ReadAsync())
    {
        workouts.Add(new WorkoutDto(
            workoutReader.GetGuid(0),
            workoutReader.GetString(1),
            workoutReader.GetDateTime(2),
            workoutReader.GetDateTime(3),
            null,
            WorkoutVisibilityValues.Global,
            workoutReader.GetInt32(4),
            [],
            workoutReader.GetString(5)));
    }
    await workoutReader.CloseAsync();

    foreach (var workout in workouts)
    {
        workout.Exercises.AddRange(await GetWorkoutExercisesAsync(connection, workout.Id));
    }

    return workouts;
}

static async Task<List<ExerciseEntryDto>> GetWorkoutExercisesAsync(SqlConnection connection, Guid workoutId)
{
    await using var command = new SqlCommand("""
        SELECT WorkoutExerciseId, ExerciseId, Name, MuscleGroup, ImageUrl, ExerciseType, Sets, Repetitions, WeightKg, DurationMinutes, DistanceKm, ElevationMeters, Difficulty
        FROM dbo.WorkoutExercise
        WHERE WorkoutId = @WorkoutId
        ORDER BY Position;
        """, connection);
    command.Parameters.AddWithValue("@WorkoutId", workoutId);
    await using var reader = await command.ExecuteReaderAsync();
    var exercises = new List<ExerciseEntryDto>();
    var exerciseIds = new List<Guid>();
    while (await reader.ReadAsync())
    {
        exerciseIds.Add(reader.GetGuid(0));
        exercises.Add(new ExerciseEntryDto(
            reader.IsDBNull(1) ? Guid.Empty : reader.GetGuid(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.IsDBNull(4) ? null : reader.GetString(4),
            reader.GetString(5),
            reader.GetInt32(6),
            reader.GetInt32(7),
            reader.GetDecimal(8),
            [],
            reader.GetInt32(9),
            reader.GetDecimal(10),
            reader.IsDBNull(11) ? null : reader.GetInt32(11),
            reader.GetInt32(12)));
    }
    await reader.CloseAsync();

    for (var index = 0; index < exercises.Count; index++)
    {
        exercises[index].SetEntries.AddRange(await GetWorkoutSetsAsync(connection, exerciseIds[index]));
    }

    return exercises;
}

static async Task<List<ExerciseSetEntryDto>> GetWorkoutSetsAsync(SqlConnection connection, Guid workoutExerciseId)
{
    await using var command = new SqlCommand("""
        SELECT Repetitions, WeightKg, Difficulty
        FROM dbo.WorkoutExerciseSet
        WHERE WorkoutExerciseId = @WorkoutExerciseId
        ORDER BY SetNumber;
        """, connection);
    command.Parameters.AddWithValue("@WorkoutExerciseId", workoutExerciseId);
    await using var reader = await command.ExecuteReaderAsync();
    var sets = new List<ExerciseSetEntryDto>();
    while (await reader.ReadAsync())
    {
        sets.Add(new ExerciseSetEntryDto(reader.GetInt32(0), reader.GetDecimal(1), reader.GetInt32(2)));
    }

    return sets;
}

static async Task SaveGlobalWorkoutAsync(IConfiguration configuration, WorkoutDto workout, Guid ownerUserId)
{
    await using var connection = new SqlConnection(GetConnectionString(configuration));
    await connection.OpenAsync();
    await using var transaction = await connection.BeginTransactionAsync();
    try
    {
        await using (var command = new SqlCommand("""
            MERGE dbo.Workout AS target
            USING (SELECT @WorkoutId AS WorkoutId) AS source
            ON target.WorkoutId = source.WorkoutId
            WHEN MATCHED THEN UPDATE SET
                OwnerUserId = @OwnerUserId,
                Name = @Name,
                PerformedAt = @PerformedAt,
                RecordedAt = @RecordedAt,
                DurationMinutes = @DurationMinutes,
                Notes = @Notes,
                Visibility = N'Global',
                UpdatedAt = SYSUTCDATETIME()
            WHEN NOT MATCHED THEN INSERT (WorkoutId, OwnerUserId, Name, PerformedAt, RecordedAt, DurationMinutes, Notes, Visibility)
            VALUES (@WorkoutId, @OwnerUserId, @Name, @PerformedAt, @RecordedAt, @DurationMinutes, @Notes, N'Global');
            """, connection, (SqlTransaction)transaction))
        {
            command.Parameters.AddWithValue("@WorkoutId", workout.Id == Guid.Empty ? Guid.NewGuid() : workout.Id);
            command.Parameters.AddWithValue("@OwnerUserId", ownerUserId);
            command.Parameters.AddWithValue("@Name", workout.Name.Trim());
            command.Parameters.AddWithValue("@PerformedAt", workout.PerformedAt.Date);
            command.Parameters.AddWithValue("@RecordedAt", workout.RecordedAt == default ? DateTime.UtcNow : workout.RecordedAt);
            command.Parameters.AddWithValue("@DurationMinutes", workout.DurationMinutes);
            command.Parameters.AddWithValue("@Notes", workout.Notes ?? string.Empty);
            await command.ExecuteNonQueryAsync();
        }

        await using (var deleteCommand = new SqlCommand("""
            DELETE sets FROM dbo.WorkoutExerciseSet sets JOIN dbo.WorkoutExercise exercise ON exercise.WorkoutExerciseId = sets.WorkoutExerciseId WHERE exercise.WorkoutId = @WorkoutId;
            DELETE FROM dbo.WorkoutExercise WHERE WorkoutId = @WorkoutId;
            """, connection, (SqlTransaction)transaction))
        {
            deleteCommand.Parameters.AddWithValue("@WorkoutId", workout.Id);
            await deleteCommand.ExecuteNonQueryAsync();
        }

        for (var position = 0; position < workout.Exercises.Count; position++)
        {
            var exercise = workout.Exercises[position];
            var workoutExerciseId = Guid.NewGuid();
            await using (var command = new SqlCommand("""
                INSERT INTO dbo.WorkoutExercise (WorkoutExerciseId, WorkoutId, ExerciseId, Position, Name, MuscleGroup, ImageUrl, ExerciseType, Sets, Repetitions, WeightKg, DurationMinutes, DistanceKm, ElevationMeters, Difficulty)
                VALUES (@WorkoutExerciseId, @WorkoutId, @ExerciseId, @Position, @Name, @MuscleGroup, @ImageUrl, @ExerciseType, @Sets, @Repetitions, @WeightKg, @DurationMinutes, @DistanceKm, @ElevationMeters, @Difficulty);
                """, connection, (SqlTransaction)transaction))
            {
                command.Parameters.AddWithValue("@WorkoutExerciseId", workoutExerciseId);
                command.Parameters.AddWithValue("@WorkoutId", workout.Id);
                command.Parameters.AddWithValue("@ExerciseId", exercise.ExerciseId == Guid.Empty ? DBNull.Value : exercise.ExerciseId);
                command.Parameters.AddWithValue("@Position", position);
                command.Parameters.AddWithValue("@Name", exercise.Name);
                command.Parameters.AddWithValue("@MuscleGroup", exercise.MuscleGroup);
                command.Parameters.AddWithValue("@ImageUrl", (object?)exercise.ImageUrl ?? DBNull.Value);
                command.Parameters.AddWithValue("@ExerciseType", exercise.ExerciseType);
                command.Parameters.AddWithValue("@Sets", exercise.SetEntries.Count > 0 ? exercise.SetEntries.Count : exercise.Sets);
                command.Parameters.AddWithValue("@Repetitions", exercise.SetEntries.FirstOrDefault()?.Repetitions ?? exercise.Repetitions);
                command.Parameters.AddWithValue("@WeightKg", exercise.SetEntries.FirstOrDefault()?.WeightKg ?? exercise.WeightKg);
                command.Parameters.AddWithValue("@DurationMinutes", exercise.DurationMinutes);
                command.Parameters.AddWithValue("@DistanceKm", exercise.DistanceKm);
                command.Parameters.AddWithValue("@ElevationMeters", (object?)exercise.ElevationMeters ?? DBNull.Value);
                command.Parameters.AddWithValue("@Difficulty", exercise.Difficulty);
                await command.ExecuteNonQueryAsync();
            }

            for (var setNumber = 0; setNumber < exercise.SetEntries.Count; setNumber++)
            {
                var set = exercise.SetEntries[setNumber];
                await using var command = new SqlCommand("""
                    INSERT INTO dbo.WorkoutExerciseSet (WorkoutExerciseId, SetNumber, Repetitions, WeightKg, Difficulty)
                    VALUES (@WorkoutExerciseId, @SetNumber, @Repetitions, @WeightKg, @Difficulty);
                    """, connection, (SqlTransaction)transaction);
                command.Parameters.AddWithValue("@WorkoutExerciseId", workoutExerciseId);
                command.Parameters.AddWithValue("@SetNumber", setNumber + 1);
                command.Parameters.AddWithValue("@Repetitions", set.Repetitions);
                command.Parameters.AddWithValue("@WeightKg", set.WeightKg);
                command.Parameters.AddWithValue("@Difficulty", set.Difficulty);
                await command.ExecuteNonQueryAsync();
            }
        }

        await transaction.CommitAsync();
    }
    catch
    {
        await transaction.RollbackAsync();
        throw;
    }
}

static async Task UpdateDisplayNameAsync(IConfiguration configuration, Guid userId, string displayName)
{
    await using var connection = new SqlConnection(GetConnectionString(configuration));
    await connection.OpenAsync();
    await using var command = new SqlCommand("UPDATE dbo.AppUser SET DisplayName = @DisplayName, UpdatedAt = SYSUTCDATETIME() WHERE UserId = @UserId;", connection);
    command.Parameters.AddWithValue("@UserId", userId);
    command.Parameters.AddWithValue("@DisplayName", displayName);
    await command.ExecuteNonQueryAsync();
}

static async Task TouchLastLoginAsync(IConfiguration configuration, Guid userId)
{
    await using var connection = new SqlConnection(GetConnectionString(configuration));
    await connection.OpenAsync();
    await using var command = new SqlCommand("UPDATE dbo.AppUser SET LastLoginAt = SYSUTCDATETIME(), UpdatedAt = SYSUTCDATETIME() WHERE UserId = @UserId;", connection);
    command.Parameters.AddWithValue("@UserId", userId);
    await command.ExecuteNonQueryAsync();
}

internal sealed record ExerciseRequest(
    Guid Id,
    string Name,
    string Description,
    List<string> MuscleGroups,
    string ExerciseType,
    string Category,
    string? ImageUrl);

internal sealed record ExerciseRecord(
    Guid Id,
    string Name,
    string Description,
    List<string> MuscleGroups,
    string ExerciseType,
    string Category,
    string? ImageUrl);

internal static class AuthenticationModes
{
    public const string Open = "Open";
    public const string Sql = "Sql";
}

internal static class Roles
{
    public const string User = "User";
    public const string Admin = "Admin";
}

internal sealed record AppUserPassword(Guid UserId, string UserName);

internal sealed record LoginRequest(string UserName, string Password);

internal sealed record CreateUserRequest(string UserName, string Password, string Role);

internal sealed record UpdateDisplayNameRequest(string DisplayName);

internal sealed record CurrentUserResponse(string AuthenticationMode, bool IsAuthenticated, string? UserName, string? DisplayName, string? Role, bool CanManageUsers);

internal sealed record UserResponse(Guid UserId, string UserName, string DisplayName, string Role, bool IsDisabled, DateTime CreatedAt, DateTime? LastLoginAt);

internal sealed record DatabaseUser(Guid UserId, string UserName, string? DisplayName, string PasswordHash, string Role, bool IsDisabled);

internal sealed record WorkoutDto(Guid Id, string Name, DateTime PerformedAt, DateTime RecordedAt, string? OwnerUserName, string Visibility, int DurationMinutes, List<ExerciseEntryDto> Exercises, string Notes);

internal sealed record ExerciseEntryDto(Guid ExerciseId, string Name, string MuscleGroup, string? ImageUrl, string ExerciseType, int Sets, int Repetitions, decimal WeightKg, List<ExerciseSetEntryDto> SetEntries, int DurationMinutes, decimal DistanceKm, int? ElevationMeters, int Difficulty);

internal sealed record ExerciseSetEntryDto(int Repetitions, decimal WeightKg, int Difficulty);

internal static class WorkoutVisibilityValues
{
    public const string Personal = "Personal";
    public const string Global = "Global";
}