// #pragma warning disable SKEXP0001
// #pragma warning disable CS0618
// #pragma warning disable CS8604

using Microsoft.EntityFrameworkCore;
using MediTender.API.Data;
using MediTender.API.Services;
using Qdrant.Client;
// using Microsoft.SemanticKernel;
// using Microsoft.SemanticKernel.Connectors.Google;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
builder.Services.AddControllers();

builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowAll",
        policy =>
        {
            policy.AllowAnyOrigin()
                  .AllowAnyMethod()
                  .AllowAnyHeader();
        });
});

builder.Services.AddScoped<IPdfParsingService, PdfParsingService>();
builder.Services.AddScoped<ITextChunkingService, TextChunkingService>();
builder.Services.AddScoped<IVectorStorageService, VectorStorageService>();
builder.Services.AddScoped<IFinancialEvaluationService, FinancialEvaluationService>();  
builder.Services.AddDbContext<ApplicationDbContext>(options => options.UseSqlServer(builder.Configuration.GetConnectionString("DefaultConnection")));
builder.Services.AddScoped<IRagService, RagService>();
builder.Services.AddScoped<IComparisonService, ComparisonService>();
builder.Services.AddScoped<IStandardExtractionService, StandardExtractionService>();
builder.Services.AddHttpClient<IGeminiService, GeminiService>();

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


// var kernelBuilder = builder.Services.AddKernel();
// kernelBuilder.AddOpenAIChatCompletion("gpt-3.5-turbo", openAiApiKey);
// kernelBuilder.AddOpenAITextEmbeddingGeneration("text-embedding-3-small", openAiApiKey);
// kernelBuilder.AddGoogleAIGeminiChatCompletion("gemini-3.5-flash", geminiApiKey);


builder.WebHost.ConfigureKestrel(options =>
{
    options.Limits.MaxRequestBodySize = 104857600; // 100 MB
});
var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseStaticFiles();
app.UseHttpsRedirection();
app.UseCors("AllowAll");
app.MapControllers();

app.Run();