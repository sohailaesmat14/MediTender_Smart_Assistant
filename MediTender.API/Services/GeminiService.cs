using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;

namespace MediTender.API.Services
{
    public interface IGeminiService
    {
        Task<string> GenerateChatResponseAsync(string prompt);
        Task<float[]> GetEmbeddingAsync(string text);
    }

    public class GeminiService : IGeminiService
    {
        private readonly string _googleApiKey;
        private readonly string _chatModel;

        public GeminiService(IConfiguration config)
        {
            _googleApiKey = config["Gemini:ApiKey"]?.Trim() ?? throw new Exception("Missing API Key");
            _chatModel = config["Gemini:ChatModel"]?.Trim() ?? "gemini-3.5-flash";
        }

        public async Task<string> GenerateChatResponseAsync(string prompt)
        {
            var url = $"https://generativelanguage.googleapis.com/v1beta/models/{_chatModel}:generateContent?key={_googleApiKey}";
            using var client = new HttpClient();
            var payload = new { contents = new[] { new { parts = new[] { new { text = prompt } } } } };
            var content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

            var response = await client.PostAsync(url, content);
            if (!response.IsSuccessStatusCode) throw new Exception($"Gemini API Error: {await response.Content.ReadAsStringAsync()}");

            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            return doc.RootElement.GetProperty("candidates")[0].GetProperty("content").GetProperty("parts")[0].GetProperty("text").GetString() ?? "";
        }

        public async Task<float[]> GetEmbeddingAsync(string text)
        {
            var url = $"https://generativelanguage.googleapis.com/v1beta/models/gemini-embedding-001:embedContent?key={_googleApiKey}";
            using var client = new HttpClient();
            var payload = new { model = "models/gemini-embedding-001", content = new { parts = new[] { new { text = text } } } };
            var content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

            var response = await client.PostAsync(url, content);
            if (!response.IsSuccessStatusCode) throw new Exception("Embedding API Error");

            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            return doc.RootElement.GetProperty("embedding").GetProperty("values").EnumerateArray().Select(v => v.GetSingle()).ToArray();
        }
    }
}