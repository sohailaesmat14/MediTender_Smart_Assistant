using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Qdrant.Client;
using MediTender.API.Models;
using MediTender.API.Data;

namespace MediTender.API.Services
{
    public class ComparisonService : IComparisonService
    {
        private readonly QdrantClient _qdrantClient;
        private readonly ApplicationDbContext _dbContext;
        private readonly string _googleApiKey;
        private readonly string _collectionName = "meditender_collection_v2";

        public ComparisonService(QdrantClient qdrantClient, ApplicationDbContext dbContext, IConfiguration config)
        {
            _qdrantClient = qdrantClient;
            _dbContext = dbContext;
            _googleApiKey = config["Gemini:ApiKey"] ?? throw new Exception("Missing API Key");
        }

        public async Task<List<ComparisonResult>> CompareOfferAsync(List<string> requirements)
        {
            var results = new List<ComparisonResult>();

            foreach (var req in requirements)
            {
                var reqEmbedding = await GetEmbeddingAsync(req);
                
                var searchResults = await _qdrantClient.SearchAsync(
                    collectionName: _collectionName,
                    vector: reqEmbedding,
                    limit: 3
                );

                var contextBuilder = new StringBuilder();
                foreach (var result in searchResults)
                {
                    if (result.Payload.TryGetValue("text", out var textValue))
                    {
                        contextBuilder.AppendLine(textValue.StringValue);
                    }
                }

                var context = contextBuilder.ToString();
                var prompt = $@"
                Evaluate the following requirement against the provided document context.
                Requirement: '{req}'
                Context: '{context}'
                
                Return ONLY a valid JSON object with the exact following keys:
                - status: 'Met', 'Partially Met', or 'Not Met'
                - evidence: Exact quote from the context supporting the status.
                - score: integer from 0 to 10.
                ";

                var aiResponse = await GenerateChatResponseAsync(prompt);
                var cleanedJson = aiResponse.Replace("```json", "").Replace("```", "").Trim();
                
                var parsedResponse = JsonSerializer.Deserialize<JsonElement>(cleanedJson);

                results.Add(new ComparisonResult
                {
                    Requirement = req,
                    IsMandatory = true, 
                    Status = parsedResponse.GetProperty("status").GetString() ?? "Not Met",
                    Evidence = parsedResponse.GetProperty("evidence").GetString() ?? "",
                    Score = parsedResponse.GetProperty("score").GetInt32()
                });
            }

            return results;
        }

        private async Task<string> GenerateChatResponseAsync(string prompt)
        {
            using var client = new HttpClient();
            var url = $"https://generativelanguage.googleapis.com/v1beta/models/gemini-1.5-flash:generateContent?key={_googleApiKey}";

            var payload = new
            {
                contents = new[]
                {
                    new { parts = new[] { new { text = prompt } } }
                }
            };

            var json = JsonSerializer.Serialize(payload);
            var content = new StringContent(json, Encoding.UTF8, "application/json");

            var response = await client.PostAsync(url, content);
            var responseString = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
                throw new Exception(responseString);

            using var doc = JsonDocument.Parse(responseString);
            return doc.RootElement
                .GetProperty("candidates")[0]
                .GetProperty("content")
                .GetProperty("parts")[0]
                .GetProperty("text")
                .GetString() ?? "";
        }

        private async Task<float[]> GetEmbeddingAsync(string text)
        {
            using var client = new HttpClient();
            var url = $"https://generativelanguage.googleapis.com/v1beta/models/gemini-embedding-001:embedContent?key={_googleApiKey}";
            
            var payload = new {
                model = "models/gemini-embedding-001",
                content = new { parts = new[] { new { text = text } } }
            };
            
            var json = JsonSerializer.Serialize(payload);
            var content = new StringContent(json, Encoding.UTF8, "application/json");

            var response = await client.PostAsync(url, content);
            var responseString = await response.Content.ReadAsStringAsync();
            
            if (!response.IsSuccessStatusCode)
                throw new Exception(responseString);

            using var doc = JsonDocument.Parse(responseString);
            return doc.RootElement
                .GetProperty("embedding")
                .GetProperty("values")
                .EnumerateArray()
                .Select(v => v.GetSingle())
                .ToArray();
        }
    }
}