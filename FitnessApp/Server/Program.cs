using System.Security.Claims;
using System.Text.Json;
using Azure.Identity;
using Azure.Storage.Blobs;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.FileProviders;
using Microsoft.Data.SqlClient;

var builder = WebApplication.CreateBuilder(args);

var appConfigurationEndpoint = builder.Configuration["AzureAppConfiguration:Endpoint"];
if (Uri.TryCreate(appConfigurationEndpoint, UriKind.Absolute, out var appConfigurationUri))
{
    builder.Configuration.AddAzureAppConfiguration(options =>
        options.Connect(appConfigurationUri, new DefaultAzureCredential()));
}

builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    options.KnownNetworks.Clear();
    options.KnownProxies.Clear();
});

builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.Cookie.Name = "FitTrack.Auth";
        options.Cookie.HttpOnly = true;
        options.Cookie.SameSite = SameSiteMode.Lax;
        options.Cookie.SecurePolicy = builder.Environment.IsDevelopment()
            ? CookieSecurePolicy.SameAsRequest
            : CookieSecurePolicy.Always;
        options.LoginPath = "/benutzer";
        options.AccessDeniedPath = "/benutzer";
        options.ExpireTimeSpan = TimeSpan.FromHours(24);
        options.SlidingExpiration = true;
    });
builder.Services.AddAuthorization();
builder.Services.AddSingleton<PasswordHasher<AppUserPassword>>();

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseHsts();
}

app.UseForwardedHeaders();
app.UseHttpsRedirection();
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

app.MapGet("/health/live", () => Results.Ok(new { status = "Healthy" }));
app.MapGet("/health", async () =>
{
    try
    {
        await using var connection = new SqlConnection(GetConnectionString(app.Configuration));
        await connection.OpenAsync();
        await using var command = new SqlCommand("SELECT 1;", connection);
        await command.ExecuteScalarAsync();
        return Results.Ok(new { status = "Healthy" });
    }
    catch (Exception exception)
    {
        app.Logger.LogError(exception, "Der Datenbank-Health-Check ist fehlgeschlagen.");
        return Results.Problem(statusCode: StatusCodes.Status503ServiceUnavailable, title: "Unhealthy");
    }
});

app.MapGet("/media/{**name}", async (string name) =>
{
    if (string.IsNullOrWhiteSpace(name)
        || name.Contains("..", StringComparison.Ordinal)
        || name.Contains('\\'))
    {
        return Results.BadRequest();
    }

    var containerUri = app.Configuration["MediaStorage:ContainerUri"];
    if (!Uri.TryCreate(containerUri, UriKind.Absolute, out var mediaContainerUri))
    {
        return Results.NotFound();
    }

    try
    {
        var container = new BlobContainerClient(mediaContainerUri, new DefaultAzureCredential());
        var download = await container.GetBlobClient(name).DownloadStreamingAsync();
        return Results.Stream(
            download.Value.Content,
            download.Value.Details.ContentType ?? "application/octet-stream",
            enableRangeProcessing: true);
    }
    catch (Azure.RequestFailedException exception) when (exception.Status == StatusCodes.Status404NotFound)
    {
        return Results.NotFound();
    }
});

app.MapGet("/api/exercises", async () => Results.Ok(await GetExercisesAsync(app.Configuration)));

app.MapPost("/api/exercises", async (ExerciseRequest request, HttpContext context) =>
{
    var validationError = Validate(request);
    if (validationError is not null)
    {
        return Results.BadRequest(validationError);
    }

    var exercise = new ExerciseRecord(
        request.Id == Guid.Empty ? Guid.NewGuid() : request.Id,
        request.Name.Trim(),
        request.Description.Trim(),
        request.MuscleGroups.Select(group => group.Trim()).Where(group => group.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
        request.ExerciseType,
        request.Category.Trim(),
        string.IsNullOrWhiteSpace(request.ImageUrl) ? null : request.ImageUrl.Trim());

    try
    {
        var createdByUserId = Guid.TryParse(context.User.FindFirstValue(ClaimTypes.NameIdentifier), out var userId)
            ? userId
            : (Guid?)null;
        await CreateExerciseAsync(app.Configuration, exercise, createdByUserId);
        return Results.Created($"/api/exercises/{exercise.Id}", exercise);
    }
    catch (SqlException exception) when (exception.Number is 2601 or 2627)
    {
        return Results.Conflict($"Eine Übung mit dem Namen „{exercise.Name}“ ist bereits vorhanden.");
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

app.MapGet("/api/profile", async (HttpContext context) =>
{
    if (GetAuthenticationMode(app.Configuration) != AuthenticationModes.Sql
        || !TryGetWorkoutOwner(context, out var userId))
    {
        return Results.Unauthorized();
    }

    return Results.Ok(await GetUserProfileAsync(app.Configuration, userId));
});

app.MapPut("/api/profile", async (UpdateUserProfileRequest request, HttpContext context) =>
{
    if (GetAuthenticationMode(app.Configuration) != AuthenticationModes.Sql
        || !TryGetWorkoutOwner(context, out var userId))
    {
        return Results.Unauthorized();
    }

    if (request.WeeklyGoal is < 1 or > 14)
    {
        return Results.BadRequest("Das Wochenziel muss zwischen 1 und 14 liegen.");
    }

    return Results.Ok(await UpdateUserProfileAsync(app.Configuration, userId, request));
});

app.MapGet("/api/stats/weekly", async (HttpContext context) =>
{
    if (GetAuthenticationMode(app.Configuration) != AuthenticationModes.Sql
        || !TryGetWorkoutOwner(context, out var userId))
    {
        return Results.Unauthorized();
    }

    return Results.Ok(await GetWeeklyStatsAsync(app.Configuration, userId));
});

app.MapGet("/api/leaderboards", async () =>
    Results.Ok(await GetLeaderboardCategoriesAsync(app.Configuration, enabledOnly: true)));

app.MapGet("/api/leaderboards/{categoryKey}", async (string categoryKey) =>
{
    var category = (await GetLeaderboardCategoriesAsync(app.Configuration, enabledOnly: true))
        .FirstOrDefault(item => string.Equals(item.CategoryKey, categoryKey, StringComparison.OrdinalIgnoreCase));
    return category is null
        ? Results.NotFound()
        : Results.Ok(await GetLeaderboardEntriesAsync(app.Configuration, category.CategoryKey));
});

app.MapGet("/api/admin/leaderboards", async (HttpContext context) =>
{
    var guard = RequireUserManagementAccess(app.Configuration, context);
    return guard ?? Results.Ok(await GetLeaderboardCategoriesAsync(app.Configuration, enabledOnly: false));
});

app.MapPut("/api/admin/leaderboards/{categoryKey}", async (string categoryKey, UpdateLeaderboardCategoryRequest request, HttpContext context) =>
{
    var guard = RequireUserManagementAccess(app.Configuration, context);
    if (guard is not null)
    {
        return guard;
    }

    if (string.IsNullOrWhiteSpace(request.DisplayName) || request.DisplayName.Trim().Length > 120
        || request.Description?.Trim().Length > 500
        || string.IsNullOrWhiteSpace(request.Unit) || request.Unit.Trim().Length > 32
        || request.SortOrder is < 0 or > 1000)
    {
        return Results.BadRequest("Bitte prüfe Name, Beschreibung, Einheit und Reihenfolge.");
    }

    var updated = await UpdateLeaderboardCategoryAsync(app.Configuration, categoryKey, request);
    return updated is null ? Results.NotFound() : Results.Ok(updated);
});

app.MapGet("/api/events", async (HttpContext context) =>
{
    if (GetAuthenticationMode(app.Configuration) != AuthenticationModes.Sql
        || !TryGetWorkoutOwner(context, out var userId))
    {
        return Results.Unauthorized();
    }

    return Results.Ok(await GetUserEventsAsync(app.Configuration, userId));
});

app.MapPost("/api/events", async (UserEventRequest request, HttpContext context) =>
{
    if (GetAuthenticationMode(app.Configuration) != AuthenticationModes.Sql
        || !TryGetWorkoutOwner(context, out var userId))
    {
        return Results.Unauthorized();
    }

    var validation = ValidateUserEvent(request);
    if (validation is not null)
    {
        return Results.BadRequest(validation);
    }

    var saved = await SaveUserEventAsync(app.Configuration, userId, Guid.NewGuid(), request);
    if (saved is null)
    {
        return Results.Problem("Das Ziel konnte nicht gespeichert werden.");
    }
    return Results.Created($"/api/events/{saved.EventId}", saved);
});

app.MapPut("/api/events/{eventId:guid}", async (Guid eventId, UserEventRequest request, HttpContext context) =>
{
    if (GetAuthenticationMode(app.Configuration) != AuthenticationModes.Sql
        || !TryGetWorkoutOwner(context, out var userId))
    {
        return Results.Unauthorized();
    }

    var validation = ValidateUserEvent(request);
    if (validation is not null)
    {
        return Results.BadRequest(validation);
    }

    var saved = await SaveUserEventAsync(app.Configuration, userId, eventId, request);
    return saved is null ? Results.NotFound() : Results.Ok(saved);
});

app.MapDelete("/api/events/{eventId:guid}", async (Guid eventId, HttpContext context) =>
{
    if (GetAuthenticationMode(app.Configuration) != AuthenticationModes.Sql
        || !TryGetWorkoutOwner(context, out var userId))
    {
        return Results.Unauthorized();
    }

    return await DeleteUserEventAsync(app.Configuration, userId, eventId)
        ? Results.NoContent()
        : Results.NotFound();
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

app.MapGet("/api/workouts", async (HttpContext context) =>
{
    if (!TryGetWorkoutOwner(context, out var ownerUserId))
    {
        return Results.Unauthorized();
    }

    return Results.Ok(await GetUserWorkoutsAsync(app.Configuration, ownerUserId));
});

app.MapPost("/api/workouts", async (WorkoutDto workout, HttpContext context) =>
{
    if (!TryGetWorkoutOwner(context, out var ownerUserId))
    {
        return Results.Unauthorized();
    }

    var saved = await SaveWorkoutAsync(app.Configuration, workout with { Visibility = WorkoutVisibilityValues.Personal }, ownerUserId);
    return Results.Ok(saved);
});

app.MapDelete("/api/workouts/{id:guid}", async (Guid id, HttpContext context) =>
{
    if (!TryGetWorkoutOwner(context, out var ownerUserId))
    {
        return Results.Unauthorized();
    }

    return await DeleteWorkoutAsync(app.Configuration, id, ownerUserId)
        ? Results.NoContent()
        : Results.NotFound();
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

    var saved = await SaveWorkoutAsync(app.Configuration, workout with { Visibility = WorkoutVisibilityValues.Global, IsTemplate = true }, ownerUserId);
    return Results.Ok(saved);
});

app.MapFallbackToFile("index.html");

if (app.Configuration.GetValue("Database:SeedExerciseCatalog", true))
{
    try
    {
        await SeedSystemExercisesAsync(app.Configuration, app.Environment);
    }
    catch (Exception exception)
    {
        app.Logger.LogError(exception, "Der optionale Übungskatalog-Abgleich ist beim Start fehlgeschlagen. Die Anwendung wird trotzdem gestartet.");
    }
}

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

static async Task<IReadOnlyList<ExerciseRecord>> GetExercisesAsync(IConfiguration configuration)
{
    await using var connection = new SqlConnection(GetConnectionString(configuration));
    await connection.OpenAsync();
    await using var command = new SqlCommand("""
        SELECT exercise.ExerciseId, exercise.Name, exercise.Description, exercise.ExerciseType,
               exercise.Category, exercise.ImageUrl, muscle.MuscleGroup
        FROM dbo.Exercise AS exercise
        LEFT JOIN dbo.ExerciseMuscleGroup AS muscle ON muscle.ExerciseId = exercise.ExerciseId
        ORDER BY exercise.Name, muscle.Position;
        """, connection);
    await using var reader = await command.ExecuteReaderAsync();
    var exercises = new Dictionary<Guid, ExerciseRecord>();
    while (await reader.ReadAsync())
    {
        var id = reader.GetGuid(0);
        if (!exercises.TryGetValue(id, out var exercise))
        {
            exercise = new ExerciseRecord(
                id,
                reader.GetString(1),
                reader.GetString(2),
                [],
                reader.GetString(3),
                reader.GetString(4),
                reader.IsDBNull(5) ? null : reader.GetString(5));
            exercises.Add(id, exercise);
        }

        if (!reader.IsDBNull(6))
            exercise.MuscleGroups.Add(reader.GetString(6));
    }

    return exercises.Values.ToList();
}

static async Task CreateExerciseAsync(IConfiguration configuration, ExerciseRecord exercise, Guid? createdByUserId)
{
    await using var connection = new SqlConnection(GetConnectionString(configuration));
    await connection.OpenAsync();
    await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync();
    await using (var command = new SqlCommand("""
        INSERT INTO dbo.Exercise
            (ExerciseId, Name, Description, ExerciseType, Category, ImageUrl, IsSystem, CreatedByUserId)
        VALUES
            (@ExerciseId, @Name, @Description, @ExerciseType, @Category, @ImageUrl, 0, @CreatedByUserId);
        """, connection, transaction))
    {
        command.Parameters.AddWithValue("@ExerciseId", exercise.Id);
        command.Parameters.AddWithValue("@Name", exercise.Name);
        command.Parameters.AddWithValue("@Description", exercise.Description);
        command.Parameters.AddWithValue("@ExerciseType", exercise.ExerciseType);
        command.Parameters.AddWithValue("@Category", exercise.Category);
        command.Parameters.AddWithValue("@ImageUrl", (object?)exercise.ImageUrl ?? DBNull.Value);
        command.Parameters.AddWithValue("@CreatedByUserId", (object?)createdByUserId ?? DBNull.Value);
        await command.ExecuteNonQueryAsync();
    }

    for (var position = 0; position < exercise.MuscleGroups.Count; position++)
    {
        await using var command = new SqlCommand("""
            INSERT INTO dbo.ExerciseMuscleGroup (ExerciseId, MuscleGroup, Position)
            VALUES (@ExerciseId, @MuscleGroup, @Position);
            """, connection, transaction);
        command.Parameters.AddWithValue("@ExerciseId", exercise.Id);
        command.Parameters.AddWithValue("@MuscleGroup", exercise.MuscleGroups[position]);
        command.Parameters.AddWithValue("@Position", position);
        await command.ExecuteNonQueryAsync();
    }

    await transaction.CommitAsync();
}

static async Task SeedSystemExercisesAsync(IConfiguration configuration, IWebHostEnvironment environment)
{
    var sourcePath = Path.GetFullPath(Path.Combine(environment.ContentRootPath, "..", "wwwroot", "data", "exercises.json"));
    var publishedPath = Path.Combine(environment.WebRootPath, "data", "exercises.json");
    var catalogPath = File.Exists(sourcePath) ? sourcePath : publishedPath;
    if (!File.Exists(catalogPath))
        throw new FileNotFoundException("Der Übungskatalog wurde nicht gefunden.", catalogPath);

    await using var stream = File.OpenRead(catalogPath);
    var exercises = await JsonSerializer.DeserializeAsync<List<ExerciseRecord>>(
        stream,
        new JsonSerializerOptions(JsonSerializerDefaults.Web)) ?? [];

    await using var connection = new SqlConnection(GetConnectionString(configuration));
    await connection.OpenAsync();
    await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync();
    foreach (var exercise in exercises)
    {
        await using (var command = new SqlCommand("""
            MERGE dbo.Exercise AS target
            USING (SELECT @Name AS Name) AS source
            ON target.Name = source.Name
            WHEN MATCHED THEN UPDATE SET
                Description = @Description,
                ExerciseType = @ExerciseType,
                Category = @Category,
                ImageUrl = @ImageUrl,
                UpdatedAt = SYSUTCDATETIME()
            WHEN NOT MATCHED THEN INSERT
                (ExerciseId, Name, Description, ExerciseType, Category, ImageUrl, IsSystem)
            VALUES
                (@ExerciseId, @Name, @Description, @ExerciseType, @Category, @ImageUrl, 1);

            DELETE FROM dbo.ExerciseMuscleGroup
            WHERE ExerciseId = (SELECT ExerciseId FROM dbo.Exercise WHERE Name = @Name);
            """, connection, transaction))
        {
            command.Parameters.AddWithValue("@ExerciseId", exercise.Id);
            command.Parameters.AddWithValue("@Name", exercise.Name);
            command.Parameters.AddWithValue("@Description", exercise.Description);
            command.Parameters.AddWithValue("@ExerciseType", exercise.ExerciseType);
            command.Parameters.AddWithValue("@Category", exercise.Category);
            command.Parameters.AddWithValue("@ImageUrl", (object?)exercise.ImageUrl ?? DBNull.Value);
            await command.ExecuteNonQueryAsync();
        }

        for (var position = 0; position < exercise.MuscleGroups.Count; position++)
        {
            await using var command = new SqlCommand("""
                INSERT INTO dbo.ExerciseMuscleGroup (ExerciseId, MuscleGroup, Position)
                SELECT ExerciseId, @MuscleGroup, @Position
                FROM dbo.Exercise
                WHERE Name = @Name;
                """, connection, transaction);
            command.Parameters.AddWithValue("@Name", exercise.Name);
            command.Parameters.AddWithValue("@MuscleGroup", exercise.MuscleGroups[position]);
            command.Parameters.AddWithValue("@Position", position);
            await command.ExecuteNonQueryAsync();
        }
    }

    await transaction.CommitAsync();
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
        SET NOCOUNT ON;
        INSERT INTO dbo.AppUser (UserId, UserName, DisplayName, PasswordHash, Role)
        VALUES (@UserId, @UserName, @UserName, @PasswordHash, @Role);

        SELECT UserId, UserName, DisplayName, Role, IsDisabled, CreatedAt, LastLoginAt
        FROM dbo.AppUser
        WHERE UserId = @UserId;
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
        SELECT WorkoutId, Name, PerformedAt, RecordedAt, DurationMinutes, Notes, IsTemplate
        FROM dbo.Workout
        WHERE Visibility = N'Global' AND IsTemplate = 1
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
            workoutReader.GetString(5),
            workoutReader.GetBoolean(6)));
    }
    await workoutReader.CloseAsync();

    foreach (var workout in workouts)
    {
        workout.Exercises.AddRange(await GetWorkoutExercisesAsync(connection, workout.Id));
    }

    return workouts;
}

static async Task<IReadOnlyList<WorkoutDto>> GetUserWorkoutsAsync(IConfiguration configuration, Guid ownerUserId)
{
    await using var connection = new SqlConnection(GetConnectionString(configuration));
    await connection.OpenAsync();
    await using var command = new SqlCommand("""
        SELECT workout.WorkoutId, workout.Name, workout.PerformedAt, workout.RecordedAt,
               userAccount.UserName, workout.Visibility, workout.DurationMinutes, workout.Notes, workout.IsTemplate
        FROM dbo.Workout AS workout
        JOIN dbo.AppUser AS userAccount ON userAccount.UserId = workout.OwnerUserId
        WHERE workout.OwnerUserId = @OwnerUserId AND workout.Visibility = N'Personal'
        ORDER BY workout.IsTemplate DESC, workout.Name, workout.PerformedAt DESC;
        """, connection);
    command.Parameters.AddWithValue("@OwnerUserId", ownerUserId);
    await using var reader = await command.ExecuteReaderAsync();
    var workouts = new List<WorkoutDto>();
    while (await reader.ReadAsync())
    {
        workouts.Add(new WorkoutDto(
            reader.GetGuid(0), reader.GetString(1), reader.GetDateTime(2), reader.GetDateTime(3),
            reader.GetString(4), reader.GetString(5), reader.GetInt32(6), [], reader.GetString(7), reader.GetBoolean(8)));
    }
    await reader.CloseAsync();

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

static async Task<WorkoutDto> SaveWorkoutAsync(IConfiguration configuration, WorkoutDto workout, Guid ownerUserId)
{
    await using var connection = new SqlConnection(GetConnectionString(configuration));
    await connection.OpenAsync();
    await using var transaction = await connection.BeginTransactionAsync();
    try
    {
        var workoutId = workout.Id == Guid.Empty ? Guid.NewGuid() : workout.Id;
        if (workout.IsTemplate)
        {
            await using var existingCommand = new SqlCommand("""
                SELECT TOP 1 WorkoutId FROM dbo.Workout
                WHERE OwnerUserId = @OwnerUserId AND IsTemplate = 1 AND Name = @Name AND Visibility = @Visibility;
                """, connection, (SqlTransaction)transaction);
            existingCommand.Parameters.AddWithValue("@OwnerUserId", ownerUserId);
            existingCommand.Parameters.AddWithValue("@Name", workout.Name.Trim());
            existingCommand.Parameters.AddWithValue("@Visibility", workout.Visibility);
            if (await existingCommand.ExecuteScalarAsync() is Guid existingId)
            {
                workoutId = existingId;
            }
        }

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
                Visibility = @Visibility,
                IsTemplate = @IsTemplate,
                UpdatedAt = SYSUTCDATETIME()
            WHEN NOT MATCHED THEN INSERT (WorkoutId, OwnerUserId, Name, PerformedAt, RecordedAt, DurationMinutes, Notes, Visibility, IsTemplate)
            VALUES (@WorkoutId, @OwnerUserId, @Name, @PerformedAt, @RecordedAt, @DurationMinutes, @Notes, @Visibility, @IsTemplate);
            """, connection, (SqlTransaction)transaction))
        {
            command.Parameters.AddWithValue("@WorkoutId", workoutId);
            command.Parameters.AddWithValue("@OwnerUserId", ownerUserId);
            command.Parameters.AddWithValue("@Name", workout.Name.Trim());
            command.Parameters.AddWithValue("@PerformedAt", workout.PerformedAt.Date);
            command.Parameters.AddWithValue("@RecordedAt", workout.RecordedAt == default ? DateTime.UtcNow : workout.RecordedAt);
            command.Parameters.AddWithValue("@DurationMinutes", workout.DurationMinutes);
            command.Parameters.AddWithValue("@Notes", workout.Notes ?? string.Empty);
            command.Parameters.AddWithValue("@Visibility", workout.Visibility);
            command.Parameters.AddWithValue("@IsTemplate", workout.IsTemplate);
            await command.ExecuteNonQueryAsync();
        }

        await using (var deleteCommand = new SqlCommand("""
            DELETE sets FROM dbo.WorkoutExerciseSet sets JOIN dbo.WorkoutExercise exercise ON exercise.WorkoutExerciseId = sets.WorkoutExerciseId WHERE exercise.WorkoutId = @WorkoutId;
            DELETE FROM dbo.WorkoutExercise WHERE WorkoutId = @WorkoutId;
            """, connection, (SqlTransaction)transaction))
        {
            deleteCommand.Parameters.AddWithValue("@WorkoutId", workoutId);
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
                command.Parameters.AddWithValue("@WorkoutId", workoutId);
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
        return workout with { Id = workoutId };
    }
    catch
    {
        await transaction.RollbackAsync();
        throw;
    }
}

static async Task<bool> DeleteWorkoutAsync(IConfiguration configuration, Guid workoutId, Guid ownerUserId)
{
    await using var connection = new SqlConnection(GetConnectionString(configuration));
    await connection.OpenAsync();
    await using var command = new SqlCommand("DELETE FROM dbo.Workout WHERE WorkoutId = @WorkoutId AND OwnerUserId = @OwnerUserId AND Visibility = N'Personal';", connection);
    command.Parameters.AddWithValue("@WorkoutId", workoutId);
    command.Parameters.AddWithValue("@OwnerUserId", ownerUserId);
    return await command.ExecuteNonQueryAsync() > 0;
}

static bool TryGetWorkoutOwner(HttpContext context, out Guid ownerUserId) =>
    Guid.TryParse(context.User.FindFirstValue(ClaimTypes.NameIdentifier), out ownerUserId);

static async Task UpdateDisplayNameAsync(IConfiguration configuration, Guid userId, string displayName)
{
    await using var connection = new SqlConnection(GetConnectionString(configuration));
    await connection.OpenAsync();
    await using var command = new SqlCommand("UPDATE dbo.AppUser SET DisplayName = @DisplayName, UpdatedAt = SYSUTCDATETIME() WHERE UserId = @UserId;", connection);
    command.Parameters.AddWithValue("@UserId", userId);
    command.Parameters.AddWithValue("@DisplayName", displayName);
    await command.ExecuteNonQueryAsync();
}

static async Task<UserProfileResponse> GetUserProfileAsync(IConfiguration configuration, Guid userId)
{
    await using var connection = new SqlConnection(GetConnectionString(configuration));
    await connection.OpenAsync();
    await using var command = new SqlCommand("""
        IF NOT EXISTS (SELECT 1 FROM dbo.UserProfile WHERE UserId = @UserId)
            INSERT INTO dbo.UserProfile (UserId) VALUES (@UserId);

        SELECT WeeklyGoal, IsLeaderboardPublic
        FROM dbo.UserProfile
        WHERE UserId = @UserId;
        """, connection);
    command.Parameters.AddWithValue("@UserId", userId);
    await using var reader = await command.ExecuteReaderAsync();
    if (!await reader.ReadAsync())
    {
        throw new InvalidOperationException("Das Benutzerprofil konnte nicht geladen werden.");
    }

    return new UserProfileResponse(reader.GetInt32(0), reader.GetBoolean(1));
}

static async Task<UserProfileResponse> UpdateUserProfileAsync(IConfiguration configuration, Guid userId, UpdateUserProfileRequest request)
{
    await using var connection = new SqlConnection(GetConnectionString(configuration));
    await connection.OpenAsync();
    await using var transaction = await connection.BeginTransactionAsync();
    await using var command = new SqlCommand("""
        UPDATE dbo.UserProfile
        SET WeeklyGoal = @WeeklyGoal,
            IsLeaderboardPublic = @IsLeaderboardPublic,
            UpdatedAt = SYSUTCDATETIME()
        WHERE UserId = @UserId;

        IF @@ROWCOUNT = 0
            INSERT INTO dbo.UserProfile (UserId, WeeklyGoal, IsLeaderboardPublic)
            VALUES (@UserId, @WeeklyGoal, @IsLeaderboardPublic);

        DECLARE @Today DATE = CONVERT(DATE, SYSUTCDATETIME());
        DECLARE @WeekStart DATE = DATEADD(DAY, -(DATEDIFF(DAY, CONVERT(DATE, '19000101', 112), @Today) % 7), @Today);

        UPDATE dbo.UserWeeklyGoal
        SET WeeklyGoal = @WeeklyGoal,
            UpdatedAt = SYSUTCDATETIME()
        WHERE UserId = @UserId AND EffectiveWeekStart = @WeekStart;

        IF @@ROWCOUNT = 0
            INSERT INTO dbo.UserWeeklyGoal (UserId, EffectiveWeekStart, WeeklyGoal)
            VALUES (@UserId, @WeekStart, @WeeklyGoal);
        """, connection, (SqlTransaction)transaction);
    command.Parameters.AddWithValue("@UserId", userId);
    command.Parameters.AddWithValue("@WeeklyGoal", request.WeeklyGoal);
    command.Parameters.AddWithValue("@IsLeaderboardPublic", request.IsLeaderboardPublic);
    try
    {
        await command.ExecuteNonQueryAsync();
        await transaction.CommitAsync();
    }
    catch
    {
        await transaction.RollbackAsync();
        throw;
    }
    return new UserProfileResponse(request.WeeklyGoal, request.IsLeaderboardPublic);
}

static async Task<WeeklyStatsResponse> GetWeeklyStatsAsync(IConfiguration configuration, Guid userId)
{
    await using var connection = new SqlConnection(GetConnectionString(configuration));
    await connection.OpenAsync();
    await using var command = new SqlCommand("""
        SELECT
            DATEADD(DAY, -(DATEDIFF(DAY, CONVERT(DATE, '19000101', 112), PerformedAt) % 7), PerformedAt) AS WeekStart,
            COUNT(*) AS SessionCount
        FROM dbo.Workout
        WHERE OwnerUserId = @UserId
          AND Visibility = N'Personal'
          AND IsTemplate = 0
        GROUP BY DATEADD(DAY, -(DATEDIFF(DAY, CONVERT(DATE, '19000101', 112), PerformedAt) % 7), PerformedAt)
        ORDER BY WeekStart;

        SELECT EffectiveWeekStart, WeeklyGoal
        FROM dbo.UserWeeklyGoal
        WHERE UserId = @UserId
        ORDER BY EffectiveWeekStart;
        """, connection);
    command.Parameters.AddWithValue("@UserId", userId);

    var sessionsByWeek = new Dictionary<DateTime, int>();
    var goals = new List<(DateTime EffectiveWeekStart, int WeeklyGoal)>();
    await using var reader = await command.ExecuteReaderAsync();
    while (await reader.ReadAsync())
    {
        sessionsByWeek[reader.GetDateTime(0).Date] = reader.GetInt32(1);
    }

    await reader.NextResultAsync();
    while (await reader.ReadAsync())
    {
        goals.Add((reader.GetDateTime(0).Date, reader.GetInt32(1)));
    }

    var today = DateTime.UtcNow.Date;
    var currentWeekStart = StartOfWeek(today);
    var currentGoal = GoalForWeek(goals, currentWeekStart);
    var currentSessions = sessionsByWeek.GetValueOrDefault(currentWeekStart);
    var successfulWeeks = sessionsByWeek
        .Where(pair => pair.Value >= GoalForWeek(goals, pair.Key))
        .Select(pair => pair.Key)
        .ToHashSet();

    var currentStreak = 0;
    var cursor = successfulWeeks.Contains(currentWeekStart) ? currentWeekStart : currentWeekStart.AddDays(-7);
    while (successfulWeeks.Contains(cursor))
    {
        currentStreak++;
        cursor = cursor.AddDays(-7);
    }

    var longestStreak = 0;
    var runningStreak = 0;
    DateTime? previousWeek = null;
    foreach (var week in successfulWeeks.Order())
    {
        runningStreak = previousWeek.HasValue && week == previousWeek.Value.AddDays(7) ? runningStreak + 1 : 1;
        longestStreak = Math.Max(longestStreak, runningStreak);
        previousWeek = week;
    }

    return new WeeklyStatsResponse(currentWeekStart, currentSessions, currentGoal, currentSessions >= currentGoal, currentStreak, longestStreak);
}

static DateTime StartOfWeek(DateTime date) => date.AddDays(-((7 + (int)date.DayOfWeek - (int)DayOfWeek.Monday) % 7)).Date;

static int GoalForWeek(IReadOnlyList<(DateTime EffectiveWeekStart, int WeeklyGoal)> goals, DateTime weekStart)
{
    var goal = goals.LastOrDefault(item => item.EffectiveWeekStart <= weekStart).WeeklyGoal;
    return goal > 0 ? goal : 3;
}

static async Task<IReadOnlyList<LeaderboardCategoryResponse>> GetLeaderboardCategoriesAsync(IConfiguration configuration, bool enabledOnly)
{
    await using var connection = new SqlConnection(GetConnectionString(configuration));
    await connection.OpenAsync();
    await using var command = new SqlCommand($"""
        SELECT CategoryKey, DisplayName, Description, Unit, IsEnabled, SortOrder
        FROM dbo.LeaderboardCategory
        {(enabledOnly ? "WHERE IsEnabled = 1" : string.Empty)}
        ORDER BY SortOrder, DisplayName;
        """, connection);
    await using var reader = await command.ExecuteReaderAsync();
    var categories = new List<LeaderboardCategoryResponse>();
    while (await reader.ReadAsync())
    {
        categories.Add(new LeaderboardCategoryResponse(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetBoolean(4), reader.GetInt32(5)));
    }
    return categories;
}

static async Task<LeaderboardCategoryResponse?> UpdateLeaderboardCategoryAsync(IConfiguration configuration, string categoryKey, UpdateLeaderboardCategoryRequest request)
{
    await using var connection = new SqlConnection(GetConnectionString(configuration));
    await connection.OpenAsync();
    await using var command = new SqlCommand("""
        UPDATE dbo.LeaderboardCategory
        SET DisplayName = @DisplayName, Description = @Description, Unit = @Unit,
            IsEnabled = @IsEnabled, SortOrder = @SortOrder, UpdatedAt = SYSUTCDATETIME()
        OUTPUT inserted.CategoryKey, inserted.DisplayName, inserted.Description, inserted.Unit, inserted.IsEnabled, inserted.SortOrder
        WHERE CategoryKey = @CategoryKey;
        """, connection);
    command.Parameters.AddWithValue("@CategoryKey", categoryKey);
    command.Parameters.AddWithValue("@DisplayName", request.DisplayName.Trim());
    command.Parameters.AddWithValue("@Description", request.Description?.Trim() ?? string.Empty);
    command.Parameters.AddWithValue("@Unit", request.Unit.Trim());
    command.Parameters.AddWithValue("@IsEnabled", request.IsEnabled);
    command.Parameters.AddWithValue("@SortOrder", request.SortOrder);
    await using var reader = await command.ExecuteReaderAsync();
    return await reader.ReadAsync()
        ? new LeaderboardCategoryResponse(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetBoolean(4), reader.GetInt32(5))
        : null;
}

static async Task<IReadOnlyList<LeaderboardEntryResponse>> GetLeaderboardEntriesAsync(IConfiguration configuration, string categoryKey)
{
    await using var connection = new SqlConnection(GetConnectionString(configuration));
    await connection.OpenAsync();
    var query = categoryKey switch
    {
        "weekly-exercises" => """
            DECLARE @Today DATE = CONVERT(DATE, SYSUTCDATETIME());
            DECLARE @WeekStart DATE = DATEADD(DAY, -(DATEDIFF(DAY, CONVERT(DATE, '19000101', 112), @Today) % 7), @Today);
            SELECT TOP 10 u.DisplayName, CONVERT(DECIMAL(18,2), COUNT(we.WorkoutExerciseId)) AS Value
            FROM dbo.AppUser u JOIN dbo.UserProfile p ON p.UserId = u.UserId
            JOIN dbo.Workout w ON w.OwnerUserId = u.UserId
            JOIN dbo.WorkoutExercise we ON we.WorkoutId = w.WorkoutId
            WHERE p.IsLeaderboardPublic = 1 AND u.IsDisabled = 0 AND w.IsTemplate = 0 AND w.Visibility = N'Personal' AND w.PerformedAt BETWEEN @WeekStart AND DATEADD(DAY, 6, @WeekStart)
            GROUP BY u.UserId, u.DisplayName ORDER BY Value DESC, u.DisplayName;
            """,
        "weekly-volume" => """
            DECLARE @Today DATE = CONVERT(DATE, SYSUTCDATETIME());
            DECLARE @WeekStart DATE = DATEADD(DAY, -(DATEDIFF(DAY, CONVERT(DATE, '19000101', 112), @Today) % 7), @Today);
            SELECT TOP 10 u.DisplayName, CONVERT(DECIMAL(18,2), SUM(CONVERT(DECIMAL(18,2), wes.Repetitions) * wes.WeightKg)) AS Value
            FROM dbo.AppUser u JOIN dbo.UserProfile p ON p.UserId = u.UserId
            JOIN dbo.Workout w ON w.OwnerUserId = u.UserId
            JOIN dbo.WorkoutExercise we ON we.WorkoutId = w.WorkoutId
            JOIN dbo.WorkoutExerciseSet wes ON wes.WorkoutExerciseId = we.WorkoutExerciseId
            WHERE p.IsLeaderboardPublic = 1 AND u.IsDisabled = 0 AND w.IsTemplate = 0 AND w.Visibility = N'Personal' AND w.PerformedAt BETWEEN @WeekStart AND DATEADD(DAY, 6, @WeekStart)
            GROUP BY u.UserId, u.DisplayName HAVING SUM(CONVERT(DECIMAL(18,2), wes.Repetitions) * wes.WeightKg) > 0 ORDER BY Value DESC, u.DisplayName;
            """,
        "weekly-running-distance" => """
            DECLARE @Today DATE = CONVERT(DATE, SYSUTCDATETIME());
            DECLARE @WeekStart DATE = DATEADD(DAY, -(DATEDIFF(DAY, CONVERT(DATE, '19000101', 112), @Today) % 7), @Today);
            SELECT TOP 10 u.DisplayName, CONVERT(DECIMAL(18,2), SUM(we.DistanceKm)) AS Value
            FROM dbo.AppUser u JOIN dbo.UserProfile p ON p.UserId = u.UserId
            JOIN dbo.Workout w ON w.OwnerUserId = u.UserId
            JOIN dbo.WorkoutExercise we ON we.WorkoutId = w.WorkoutId
            WHERE p.IsLeaderboardPublic = 1 AND u.IsDisabled = 0 AND w.IsTemplate = 0 AND w.Visibility = N'Personal'
                AND we.ExerciseType = N'Endurance' AND we.Name IN (N'Laufen', N'Trailrun', N'Wandern')
                AND w.PerformedAt BETWEEN @WeekStart AND DATEADD(DAY, 6, @WeekStart)
            GROUP BY u.UserId, u.DisplayName HAVING SUM(we.DistanceKm) > 0 ORDER BY Value DESC, u.DisplayName;
            """,
        "current-weekly-streak" => null,
        _ => throw new ArgumentOutOfRangeException(nameof(categoryKey))
    };

    if (query is null)
    {
        return await GetStreakLeaderboardAsync(configuration);
    }

    await using var command = new SqlCommand(query, connection);
    await using var reader = await command.ExecuteReaderAsync();
    var entries = new List<LeaderboardEntryResponse>();
    var position = 1;
    while (await reader.ReadAsync())
    {
        entries.Add(new LeaderboardEntryResponse(position++, reader.IsDBNull(0) ? "Sportler" : reader.GetString(0), reader.GetDecimal(1)));
    }
    return entries;
}

static async Task<IReadOnlyList<LeaderboardEntryResponse>> GetStreakLeaderboardAsync(IConfiguration configuration)
{
    await using var connection = new SqlConnection(GetConnectionString(configuration));
    await connection.OpenAsync();
    await using var command = new SqlCommand("""
        SELECT u.UserId, u.DisplayName
        FROM dbo.AppUser u JOIN dbo.UserProfile p ON p.UserId = u.UserId
        WHERE p.IsLeaderboardPublic = 1 AND u.IsDisabled = 0;
        """, connection);
    await using var reader = await command.ExecuteReaderAsync();
    var users = new List<(Guid UserId, string DisplayName)>();
    while (await reader.ReadAsync())
    {
        users.Add((reader.GetGuid(0), reader.IsDBNull(1) ? "Sportler" : reader.GetString(1)));
    }

    var values = new List<(string DisplayName, int Value)>();
    foreach (var user in users)
    {
        var stats = await GetWeeklyStatsAsync(configuration, user.UserId);
        if (stats.CurrentStreak > 0) values.Add((user.DisplayName, stats.CurrentStreak));
    }
    return values.OrderByDescending(item => item.Value).ThenBy(item => item.DisplayName).Take(10)
        .Select((item, index) => new LeaderboardEntryResponse(index + 1, item.DisplayName, item.Value)).ToList();
}

static string? ValidateUserEvent(UserEventRequest request)
{
    if (string.IsNullOrWhiteSpace(request.Title) || request.Title.Trim().Length > 160) return "Der Titel ist erforderlich und darf höchstens 160 Zeichen enthalten.";
    if (request.EventType is not ("Competition" or "Running" or "PersonalGoal" or "Other")) return "Der Ereignistyp ist ungültig.";
    if ((request.Description?.Length ?? 0) > 1000) return "Die Beschreibung darf höchstens 1000 Zeichen enthalten.";
    return null;
}

static async Task<IReadOnlyList<UserEventResponse>> GetUserEventsAsync(IConfiguration configuration, Guid userId)
{
    await using var connection = new SqlConnection(GetConnectionString(configuration));
    await connection.OpenAsync();
    await using var command = new SqlCommand("""
        SELECT EventId, Title, EventType, EventDate, Description, IsCompleted
        FROM dbo.UserEvent WHERE OwnerUserId = @UserId ORDER BY IsCompleted, EventDate, Title;
        """, connection);
    command.Parameters.AddWithValue("@UserId", userId);
    await using var reader = await command.ExecuteReaderAsync();
    var events = new List<UserEventResponse>();
    while (await reader.ReadAsync())
    {
        events.Add(new UserEventResponse(reader.GetGuid(0), reader.GetString(1), reader.GetString(2), reader.GetDateTime(3), reader.GetString(4), reader.GetBoolean(5)));
    }
    return events;
}

static async Task<UserEventResponse?> SaveUserEventAsync(IConfiguration configuration, Guid userId, Guid eventId, UserEventRequest request)
{
    await using var connection = new SqlConnection(GetConnectionString(configuration));
    await connection.OpenAsync();
    await using var command = new SqlCommand("""
        IF EXISTS (SELECT 1 FROM dbo.UserEvent WHERE EventId = @EventId)
        BEGIN
            UPDATE dbo.UserEvent SET Title=@Title, EventType=@EventType, EventDate=@EventDate, Description=@Description,
                IsCompleted=@IsCompleted, CompletedAt=CASE WHEN @IsCompleted=1 THEN COALESCE(CompletedAt, SYSUTCDATETIME()) ELSE NULL END, UpdatedAt=SYSUTCDATETIME()
            WHERE EventId=@EventId AND OwnerUserId=@UserId;
            IF @@ROWCOUNT = 0 RETURN;
        END
        ELSE
            INSERT INTO dbo.UserEvent (EventId, OwnerUserId, Title, EventType, EventDate, Description, IsCompleted, CompletedAt)
            VALUES (@EventId, @UserId, @Title, @EventType, @EventDate, @Description, @IsCompleted, CASE WHEN @IsCompleted=1 THEN SYSUTCDATETIME() ELSE NULL END);
        SELECT EventId, Title, EventType, EventDate, Description, IsCompleted FROM dbo.UserEvent WHERE EventId=@EventId AND OwnerUserId=@UserId;
        """, connection);
    command.Parameters.AddWithValue("@EventId", eventId); command.Parameters.AddWithValue("@UserId", userId);
    command.Parameters.AddWithValue("@Title", request.Title.Trim()); command.Parameters.AddWithValue("@EventType", request.EventType);
    command.Parameters.AddWithValue("@EventDate", request.EventDate.Date); command.Parameters.AddWithValue("@Description", request.Description?.Trim() ?? string.Empty);
    command.Parameters.AddWithValue("@IsCompleted", request.IsCompleted);
    await using var reader = await command.ExecuteReaderAsync();
    return await reader.ReadAsync() ? new UserEventResponse(reader.GetGuid(0), reader.GetString(1), reader.GetString(2), reader.GetDateTime(3), reader.GetString(4), reader.GetBoolean(5)) : null;
}

static async Task<bool> DeleteUserEventAsync(IConfiguration configuration, Guid userId, Guid eventId)
{
    await using var connection = new SqlConnection(GetConnectionString(configuration)); await connection.OpenAsync();
    await using var command = new SqlCommand("DELETE FROM dbo.UserEvent WHERE EventId=@EventId AND OwnerUserId=@UserId;", connection);
    command.Parameters.AddWithValue("@EventId", eventId); command.Parameters.AddWithValue("@UserId", userId);
    return await command.ExecuteNonQueryAsync() > 0;
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

internal sealed record UpdateUserProfileRequest(int WeeklyGoal, bool IsLeaderboardPublic);

internal sealed record UserProfileResponse(int WeeklyGoal, bool IsLeaderboardPublic);

internal sealed record WeeklyStatsResponse(DateTime WeekStart, int SessionCount, int WeeklyGoal, bool GoalReached, int CurrentStreak, int LongestStreak);
internal sealed record LeaderboardCategoryResponse(string CategoryKey, string DisplayName, string Description, string Unit, bool IsEnabled, int SortOrder);
internal sealed record UpdateLeaderboardCategoryRequest(string DisplayName, string? Description, string Unit, bool IsEnabled, int SortOrder);
internal sealed record LeaderboardEntryResponse(int Position, string DisplayName, decimal Value);
internal sealed record UserEventRequest(string Title, string EventType, DateTime EventDate, string? Description, bool IsCompleted);
internal sealed record UserEventResponse(Guid EventId, string Title, string EventType, DateTime EventDate, string Description, bool IsCompleted);

internal sealed record CurrentUserResponse(string AuthenticationMode, bool IsAuthenticated, string? UserName, string? DisplayName, string? Role, bool CanManageUsers);

internal sealed record UserResponse(Guid UserId, string UserName, string DisplayName, string Role, bool IsDisabled, DateTime CreatedAt, DateTime? LastLoginAt);

internal sealed record DatabaseUser(Guid UserId, string UserName, string? DisplayName, string PasswordHash, string Role, bool IsDisabled);

internal sealed record WorkoutDto(Guid Id, string Name, DateTime PerformedAt, DateTime RecordedAt, string? OwnerUserName, string Visibility, int DurationMinutes, List<ExerciseEntryDto> Exercises, string Notes, bool IsTemplate);

internal sealed record ExerciseEntryDto(Guid ExerciseId, string Name, string MuscleGroup, string? ImageUrl, string ExerciseType, int Sets, int Repetitions, decimal WeightKg, List<ExerciseSetEntryDto> SetEntries, int DurationMinutes, decimal DistanceKm, int? ElevationMeters, int Difficulty);

internal sealed record ExerciseSetEntryDto(int Repetitions, decimal WeightKg, int Difficulty);

internal static class WorkoutVisibilityValues
{
    public const string Personal = "Personal";
    public const string Global = "Global";
}