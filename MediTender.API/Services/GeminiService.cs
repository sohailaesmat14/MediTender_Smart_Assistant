using System.Text;
using System.Text.Json;
using Qdrant.Client;
using Qdrant.Client.Grpc;
using MediTender.API.Models;
using MediTender.API.Data;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;

namespace MediTender.API.Services
{
    public class GeminiService : IGeminiService
    {
        private readonly HttpClient _httpClient;
        private readonly string _googleApiKey;
        private readonly string _chatModel = "gemini-flash-latest";
        private readonly ILogger<GeminiService> _logger;

        public GeminiService(HttpClient httpClient, IConfiguration configuration, ILogger<GeminiService> logger) 
        {
            _googleApiKey = configuration["Gemini:ApiKey"] ?? throw new ArgumentNullException("Gemini API Key is missing in appsettings.json!");
            _httpClient = httpClient;
            _logger = logger; 
        }

        public async Task<string> GenerateChatResponseAsync(string prompt, CancellationToken cancellationToken = default)
        {
            var url = $"https://generativelanguage.googleapis.com/v1beta/models/{_chatModel}:generateContent?key={_googleApiKey}";
            var payload = new { contents = new[] { new { parts = new[] { new { text = prompt } } } } };
            var content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

            cancellationToken.ThrowIfCancellationRequested();

            var response = await _httpClient.PostAsync(url, content, cancellationToken);
            
            response.EnsureSuccessStatusCode();

            var responseString = await response.Content.ReadAsStringAsync(cancellationToken);
            using var doc = JsonDocument.Parse(responseString);
            return doc.RootElement.GetProperty("candidates")[0].GetProperty("content").GetProperty("parts")[0].GetProperty("text").GetString() ?? "";
        }   

        public async Task<float[]> GetEmbeddingAsync(string text, CancellationToken cancellationToken = default)
        {
            var url = $"https://generativelanguage.googleapis.com/v1beta/models/gemini-embedding-001:embedContent?key={_googleApiKey}";
            var payload = new { model = "models/gemini-embedding-001", content = new { parts = new[] { new { text = text } } } };
            var content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

            cancellationToken.ThrowIfCancellationRequested();
            
            var response = await _httpClient.PostAsync(url, content, cancellationToken);
            
            response.EnsureSuccessStatusCode();

            var responseString = await response.Content.ReadAsStringAsync(cancellationToken);
            using var doc = JsonDocument.Parse(responseString);
            return doc.RootElement.GetProperty("embedding").GetProperty("values").EnumerateArray().Select(v => v.GetSingle()).ToArray();
        }
        public async Task<List<float[]>> GetEmbeddingsBatchAsync(List<string> texts, CancellationToken cancellationToken = default)
        {
            var allEmbeddings = new List<float[]>();
            int batchSize = 15;

            for (int i = 0; i < texts.Count; i += batchSize)
            {
                var batch = texts.Skip(i).Take(batchSize).ToList();
                
                var requests = batch.Select(text => new { text = text }).ToList();
                var payload = new 
                { 
                    requests = requests.Select(r => new { model = "models/gemini-embedding-001", content = new { parts = new[] { new { text = r.text } } } }) 
                };

                var url = $"https://generativelanguage.googleapis.com/v1beta/models/gemini-embedding-001:batchEmbedContents?key={_googleApiKey}";
                var content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

                cancellationToken.ThrowIfCancellationRequested();

                var response = await _httpClient.PostAsync(url, content, cancellationToken);
                
                response.EnsureSuccessStatusCode();

                var responseString = await response.Content.ReadAsStringAsync(cancellationToken);
                using var doc = JsonDocument.Parse(responseString);
                
                foreach (var embedding in doc.RootElement.GetProperty("embeddings").EnumerateArray())
                {
                    allEmbeddings.Add(embedding.GetProperty("values").EnumerateArray().Select(v => v.GetSingle()).ToArray());
                }

                await Task.Delay(2000, cancellationToken); 
            }

            return allEmbeddings;
        }    
    }
}