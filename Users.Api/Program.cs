using Dapper;
using Npgsql;
using Prometheus;

var builder = WebApplication.CreateBuilder(args);

// ------------------------------------------------------
// CORS
// ------------------------------------------------------
builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowAll", policy =>
    {
        policy.AllowAnyOrigin()
              .AllowAnyMethod()
              .AllowAnyHeader();
    });
});

// Swagger
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

// Connection string do appsettings
var connectionString = builder.Configuration.GetConnectionString("DefaultConnection");

// ------------------------------------------------------
// 1. Criar banco automaticamente
// ------------------------------------------------------
async Task EnsureDatabaseExists()
{
    var csBuilder = new NpgsqlConnectionStringBuilder(connectionString);
    var targetDb = csBuilder.Database;  // lê o DB real que queremos

    // Conecta no postgres para poder criar DB
    csBuilder.Database = "postgres";

    using var con = new NpgsqlConnection(csBuilder.ConnectionString);
    await con.OpenAsync();

    var exists = await con.ExecuteScalarAsync<int>(
        "SELECT 1 FROM pg_database WHERE datname=@n",
        new { n = targetDb });

    if (exists == 0)
    {
        await con.ExecuteAsync($"CREATE DATABASE \"{targetDb}\"");
        Console.WriteLine($"Banco criado automaticamente: {targetDb}");
    }
}

// ------------------------------------------------------
// 2. Criar tabela
// ------------------------------------------------------
async Task EnsureTablesExist()
{
    using var con = new NpgsqlConnection(connectionString);
    await con.OpenAsync();

    var sql = """
        CREATE TABLE IF NOT EXISTS users(
            id UUID PRIMARY KEY,
            email TEXT NOT NULL UNIQUE,
            name TEXT NOT NULL,
            created_at TIMESTAMPTZ NOT NULL
        );
    """;

    await con.ExecuteAsync(sql);
}

// Executar migrações
await EnsureDatabaseExists();
await EnsureTablesExist();

// ------------------------------------------------------
// 3. Construção do app
// ------------------------------------------------------
var app = builder.Build();

app.UseCors("AllowAll");     // 👈 CORS deve vir antes dos endpoints
app.UseSwagger();
app.UseSwaggerUI();

// ------------------------------------------------------
// 4. Endpoints
// ------------------------------------------------------

app.MapPost("/users/register", async (UserCreate dto) =>
{
    if (string.IsNullOrWhiteSpace(dto.Email) || string.IsNullOrWhiteSpace(dto.Name))
        return Results.BadRequest("Email e Name são obrigatórios.");

    using var con = new NpgsqlConnection(connectionString);
    await con.OpenAsync();

    var user = new
    {
        id = Guid.NewGuid(),
        email = dto.Email.Trim(),
        name = dto.Name.Trim(),
        created_at = DateTime.UtcNow
    };

    await con.ExecuteAsync(
        "INSERT INTO users (id,email,name,created_at) VALUES (@id,@email,@name,@created_at)",
        user);

    return Results.Created($"/users/{user.id}", user);
});

app.MapGet("/users/{id}", async (Guid id) =>
{
    using var con = new NpgsqlConnection(connectionString);
    await con.OpenAsync();

    var user = await con.QuerySingleOrDefaultAsync(
        "SELECT id,email,name,created_at FROM users WHERE id=@id",
        new { id });

    return user is null ? Results.NotFound() : Results.Ok(user);
});

app.MapGet("/users", async () =>
{
    using var con = new NpgsqlConnection(connectionString);
    var list = await con.QueryAsync("SELECT id,email,name,created_at FROM users ORDER BY created_at DESC");
    return Results.Ok(list);
});

app.MapPut("/users/{id}", async (Guid id, UserCreate dto) =>
{
    if (string.IsNullOrWhiteSpace(dto.Email) || string.IsNullOrWhiteSpace(dto.Name))
        return Results.BadRequest("Email e Name são obrigatórios.");

    using var con = new NpgsqlConnection(connectionString);
    await con.OpenAsync();

    var rows = await con.ExecuteAsync(
        "UPDATE users SET email = @Email, name = @Name WHERE id = @id",
        new { id, dto.Email, dto.Name });

    return rows == 0 ? Results.NotFound() : Results.Ok(new { updated = true });
});

app.MapDelete("/users/{id}", async (Guid id) =>
{
    using var con = new NpgsqlConnection(connectionString);
    await con.OpenAsync();

    var rows = await con.ExecuteAsync(
        "DELETE FROM users WHERE id = @id",
        new { id });

    return rows == 0 ? Results.NotFound() : Results.Ok(new { deleted = true });
});

// endpoint de métricas do Prometheus
app.MapMetrics();

app.Run();

record UserCreate(string Email, string Name);
