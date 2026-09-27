using Api;
using FastEndpoints;
using Scalar.AspNetCore;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddNpgsqlDataSource(builder.Configuration.GetConnectionString("Postgres")!);

var seaweedFsPublicUrl = builder.Configuration["SeaweedFs:PublicUrl"];
if (string.IsNullOrEmpty(seaweedFsPublicUrl))
{
    throw new InvalidOperationException("SeaweedFs:PublicUrl configuration is required.");
}
builder.Services.AddSingleton(new SeaweedFsOptions(seaweedFsPublicUrl));

builder.Services.AddFastEndpoints();

builder.Services.AddOpenApi();

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
    app.MapScalarApiReference();
}

app.UseFastEndpoints();

app.Run();