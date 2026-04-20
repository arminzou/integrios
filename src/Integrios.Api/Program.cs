using Integrios.Api.Infrastructure.Data;
using Npgsql;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddOpenApi();

var postgresConnectionString = builder.Configuration.GetConnectionString("Postgres");
if (string.IsNullOrWhiteSpace(postgresConnectionString))
{
    throw new InvalidOperationException("ConnectionStrings:Postgres is required.");
}

builder.Services.AddSingleton(_ =>
{
    var dataSourceBuilder = new NpgsqlDataSourceBuilder(postgresConnectionString);
    return dataSourceBuilder.Build();
});
builder.Services.AddSingleton<IDbConnectionFactory, NpgsqlConnectionFactory>();


var app = builder.Build();

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseHttpsRedirection();

app.Run();
