using Microsoft.EntityFrameworkCore;
using MediTender.API.Data;
using MediTender.API.Services; 
using Microsoft.SemanticKernel.Memory;
using Microsoft.SemanticKernel.Connectors.Qdrant;
using Microsoft.SemanticKernel.Connectors.OpenAI;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddOpenApi();
builder.Services.AddControllers();
builder.Services.AddScoped<IPdfParsingService, PdfParsingService>();
builder.Services.AddScoped<ITextChunkingService, TextChunkingService>();
builder.Services.AddDbContext<ApplicationDbContext>(options =>options.UseSqlServer(builder.Configuration.GetConnectionString("DefaultConnection")));

var openAiApiKey = builder.Configuration["OpenAI:ApiKey"];
var qdrantEndpoint = builder.Configuration["Qdrant:Endpoint"];
var qdrantApiKey = builder.Configuration["Qdrant:ApiKey"];

var qdrantMemoryStore = new QdrantMemoryStore(qdrantEndpoint, 1536, apiKey: qdrantApiKey);

var semanticTextMemory = new MemoryBuilder()
    .WithOpenAITextEmbeddingGeneration("text-embedding-3-small", openAiApiKey)
    .WithMemoryStore(qdrantMemoryStore)
    .Build();

builder.Services.AddSingleton<ISemanticTextMemory>(semanticTextMemory);

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseStaticFiles();
app.UseHttpsRedirection();
app.MapControllers();

var summaries = new[]
{
    "Freezing", "Bracing", "Chilly", "Cool", "Mild", "Warm", "Balmy", "Hot", "Sweltering", "Scorching"
};

app.MapGet("/weatherforecast", () =>
{
    var forecast =  Enumerable.Range(1, 5).Select(index =>
        new WeatherForecast
        (
            DateOnly.FromDateTime(DateTime.Now.AddDays(index)),
            Random.Shared.Next(-20, 55),
            summaries[Random.Shared.Next(summaries.Length)]
        ))
        .ToArray();
    return forecast;
})
.WithName("GetWeatherForecast");

app.Run();

record WeatherForecast(DateOnly Date, int TemperatureC, string? Summary)
{
    public int TemperatureF => 32 + (int)(TemperatureC / 0.5556);
}
