using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Qdrant.Client;
using Qdrant.Client.Grpc;

namespace MediTender.API.Services
{
    public class StandardExtractionService : IStandardExtractionService
    {
        private readonly QdrantClient _qdrantClient;
        private readonly string _googleApiKey;
        private readonly string _collectionName = "meditender_collection_v2";

        public StandardExtractionService(QdrantClient qdrantClient, IConfiguration config)
        {
            _qdrantClient = qdrantClient;
            _googleApiKey = config["Gemini:ApiKey"] ?? throw new Exception("Missing API Key");
        }

        public async Task<List<string>> ExtractRequirementsAsync(string fileName)
        {
            var searchVector = await GetEmbeddingAsync("mandatory technical specifications, requirements, physical characteristics, performance parameters");

            var filter = new Filter();
            filter.Must.Add(new Condition
            {
                Field = new FieldCondition
                {
                    Key = "fileName",
                    Match = new Match { Text = fileName }
                }
            });

            var searchResults = await _qdrantClient.SearchAsync(
                collectionName: _collectionName,
                vector: searchVector,
                filter: filter,
                limit: 10
            );

            var contextBuilder = new StringBuilder();
            foreach (var result in searchResults)
            {
                if (result.Payload.TryGetValue("text", out var textValue))
                {
                    contextBuilder.AppendLine(textValue.StringValue);
                }
            }

            var prompt = $@"
            You are a Biomedical Equipment Expert. Extract all the mandatory technical specifications and requirements from the following text.
            Return ONLY a valid JSON array of strings. Each string is a single requirement.
            Do not include any markdown formatting like ```json.

            Context:
            {contextBuilder.ToString()}
            ";

            var aiResponse = await GenerateChatResponseAsync(prompt);
            var cleanedJson = aiResponse.Replace("```json", "").Replace("```", "").Trim();
            
            var requirements = JsonSerializer.Deserialize<List<string>>(cleanedJson);
            return requirements ?? new List<string>();
        }

        private async Task<string> GenerateChatResponseAsync(string prompt)
        {
            using var client = new HttpClient();
            var url = $"[https://generativelanguage.googleapis.com/v1beta/models/gemini-3.5-flash:generateContent?key=](https://generativelanguage.googleapis.com/v1beta/models/gemini-3.5-flash:generateContent?key=){_googleApiKey}";

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
            var url = $"[https://generativelanguage.googleapis.com/v1beta/models/gemini-embedding-001:embedContent?key=](https://generativelanguage.googleapis.com/v1beta/models/gemini-embedding-001:embedContent?key=){_googleApiKey}";
            
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