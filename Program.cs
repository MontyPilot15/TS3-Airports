using Microsoft.Data.Sqlite;
using System.Text.Json;

var builder = WebApplication.CreateBuilder(args);

// Allow CORS for the client (adjust origins in production)
builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
    {
        policy.AllowAnyOrigin()
              .AllowAnyMethod()
              .AllowAnyHeader();
    });
});

var app = builder.Build();

app.UseCors();

// Ensure database exists
var connectionString = "Data Source=filecommon.db";
EnsureDatabase(connectionString);

app.MapPost("/api/upload", async (UploadRequest req) =>
{
    if (string.IsNullOrWhiteSpace(req.GroupCode) || string.IsNullOrWhiteSpace(req.UserName))
        return Results.BadRequest("GroupCode and UserName are required.");

    var filesJson = JsonSerializer.Serialize(req.Files ?? Array.Empty<string>());

    await using var conn = new SqliteConnection(connectionString);
    await conn.OpenAsync();

    // Upsert: delete old and insert new for this user+group
    var deleteCmd = conn.CreateCommand();
    deleteCmd.CommandText = "DELETE FROM Uploads WHERE GroupCode = $group AND UserName = $user";
    deleteCmd.Parameters.AddWithValue("$group", req.GroupCode);
    deleteCmd.Parameters.AddWithValue("$user", req.UserName);
    await deleteCmd.ExecuteNonQueryAsync();

    var insertCmd = conn.CreateCommand();
    insertCmd.CommandText = @"
        INSERT INTO Uploads (GroupCode, UserName, FilesJson, LastUpdated)
        VALUES ($group, $user, $files, $updated)";
    insertCmd.Parameters.AddWithValue("$group", req.GroupCode);
    insertCmd.Parameters.AddWithValue("$user", req.UserName);
    insertCmd.Parameters.AddWithValue("$files", filesJson);
    insertCmd.Parameters.AddWithValue("$updated", DateTime.UtcNow.ToString("o"));
    await insertCmd.ExecuteNonQueryAsync();

    return Results.Ok(new { message = "Uploaded successfully", fileCount = req.Files?.Length ?? 0 });
});

app.MapGet("/api/common/{groupCode}", async (string groupCode) =>
{
    if (string.IsNullOrWhiteSpace(groupCode))
        return Results.BadRequest("GroupCode is required.");

    await using var conn = new SqliteConnection(connectionString);
    await conn.OpenAsync();

    var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT UserName, FilesJson FROM Uploads WHERE GroupCode = $group";
    cmd.Parameters.AddWithValue("$group", groupCode);

    var sets = new List<HashSet<string>>();
    await using var reader = await cmd.ExecuteReaderAsync();
    while (await reader.ReadAsync())
    {
        var json = reader.GetString(1);
        var files = JsonSerializer.Deserialize<string[]>(json) ?? Array.Empty<string>();
        sets.Add(new HashSet<string>(files, StringComparer.OrdinalIgnoreCase));
    }

    if (sets.Count == 0)
        return Results.Ok(new { commonFiles = Array.Empty<string>(), userCount = 0 });

    // Intersection of all sets
    var common = sets[0];
    for (int i = 1; i < sets.Count; i++)
        common.IntersectWith(sets[i]);

    return Results.Ok(new
    {
        commonFiles = common.OrderBy(f => f).ToArray(),
        userCount = sets.Count
    });
});

app.MapGet("/api/users/{groupCode}", async (string groupCode) =>
{
    await using var conn = new SqliteConnection(connectionString);
    await conn.OpenAsync();

    var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT UserName, LastUpdated FROM Uploads WHERE GroupCode = $group ORDER BY UserName";
    cmd.Parameters.AddWithValue("$group", groupCode);

    var users = new List<object>();
    await using var reader = await cmd.ExecuteReaderAsync();
    while (await reader.ReadAsync())
    {
        users.Add(new
        {
            userName = reader.GetString(0),
            lastUpdated = reader.GetString(1)
        });
    }

    return Results.Ok(users);
});

app.MapGet("/", () => Results.Ok(new { status = "FileCommon API is running", time = DateTime.UtcNow }));

app.Run();

static void EnsureDatabase(string connectionString)
{
    using var conn = new SqliteConnection(connectionString);
    conn.Open();
    var cmd = conn.CreateCommand();
    cmd.CommandText = @"
        CREATE TABLE IF NOT EXISTS Uploads (
            Id INTEGER PRIMARY KEY AUTOINCREMENT,
            GroupCode TEXT NOT NULL,
            UserName TEXT NOT NULL,
            FilesJson TEXT NOT NULL,
            LastUpdated TEXT NOT NULL,
            UNIQUE(GroupCode, UserName)
        );
        CREATE INDEX IF NOT EXISTS IX_Uploads_GroupCode ON Uploads(GroupCode);
    ";
    cmd.ExecuteNonQuery();
}

record UploadRequest(string GroupCode, string UserName, string[] Files);
