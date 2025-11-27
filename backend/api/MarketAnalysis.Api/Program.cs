using System.Net.Http.Json;

var builder = WebApplication.CreateBuilder(args);

// CORS for React
builder.Services.AddCors();

// HttpClient for ML service
builder.Services.AddHttpClient("MlService", client =>
{
    client.BaseAddress = new Uri("http://localhost:8000");
});

var app = builder.Build();

// For now, turn OFF HTTPS redirection to avoid redirect weirdness
// app.UseHttpsRedirection();

app.UseCors(policy =>
    policy
        .WithOrigins("http://localhost:5173")
        .AllowAnyHeader()
        .AllowAnyMethod()
);

// Simple health endpoint
app.MapGet("/api/health", () =>
{
    return Results.Ok(new { status = "ok" });
});

// ML dummy endpoint
app.MapGet("/api/ml/dummy", async (IHttpClientFactory httpClientFactory) =>
{
    var client = httpClientFactory.CreateClient("MlService");

    var response = await client.PostAsJsonAsync("/predict", new { ticker = "AAPL" });

    if (!response.IsSuccessStatusCode)
    {
        return Results.Problem("ML service error");
    }

    var mlResult = await response.Content.ReadFromJsonAsync<object>();
    return Results.Ok(mlResult);
});

app.Run();
