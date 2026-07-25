#pragma warning disable SKEXP0001
#pragma warning disable CS0618
#pragma warning disable CS8604

using Microsoft.EntityFrameworkCore;
using MediTender.API.Data;
using MediTender.API.Services;
using Qdrant.Client;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.Connectors.Google;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddOpenApi();
builder.Services.AddControllers();

builder.Services.AddScoped<IPdfParsingService, PdfParsingService>();
builder.Services.AddScoped<ITextChunkingService, TextChunkingService>();
builder.Services.AddScoped<IVectorStorageService, VectorStorageService>();
builder.Services.AddDbContext<ApplicationDbContext>(options => options.UseSqlServer(builder.Configuration.GetConnectionString("DefaultConnection")));
builder.Services.AddScoped<IRagService, RagService>();

// var openAiApiKey = builder.Configuration["OpenAI:ApiKey"] ?? throw new ArgumentNullException("OpenAI:ApiKey");
var geminiApiKey = builder.Configuration["Gemini:ApiKey"] ?? throw new ArgumentNullException("Gemini:ApiKey");
var qdrantEndpoint = builder.Configuration["Qdrant:Endpoint"] ?? throw new ArgumentNullException("Qdrant:Endpoint");
var qdrantApiKey = builder.Configuration["Qdrant:ApiKey"] ?? throw new ArgumentNullException("Qdrant:ApiKey");

var qdrantClient = new QdrantClient(
    host: new Uri(qdrantEndpoint).Host,
    https: true,
    apiKey: qdrantApiKey
);
builder.Services.AddSingleton(qdrantClient);

var kernelBuilder = builder.Services.AddKernel();
// kernelBuilder.AddOpenAIChatCompletion("gpt-3.5-turbo", openAiApiKey);
// kernelBuilder.AddOpenAITextEmbeddingGeneration("text-embedding-3-small", openAiApiKey);

kernelBuilder.AddGoogleAIGeminiChatCompletion("gemini-1.5-flash", geminiApiKey);

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseStaticFiles();
app.UseHttpsRedirection();
app.MapControllers();

app.Run();