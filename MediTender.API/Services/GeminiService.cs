using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;

namespace MediTender.API.Services
{
    public interface IGeminiService
    {
        Task<string> GenerateChatResponseAsync(string prompt);
        Task<float[]> GetEmbeddingAsync(string text);
        Task<List<float[]>> GetEmbeddingsBatchAsync(List<string> texts); 
    }

    public class GeminiService : IGeminiService
    {
        private readonly string _googleApiKey;
        private readonly string _chatModel;
        private readonly HttpClient _httpClient;

        public GeminiService(IConfiguration config, HttpClient httpClient)
        {
            _googleApiKey = config["Gemini:ApiKey"]?.Trim() ?? throw new Exception("Missing API Key");
            _chatModel = config["Gemini:ChatModel"]?.Trim() ?? "gemini-flash-latest";
            _httpClient = httpClient;
        }

        public async Task<string> GenerateChatResponseAsync(string prompt)
        {
            var url = $"https://generativelanguage.googleapis.com/v1beta/models/{_chatModel}:generateContent?key={_googleApiKey}";
            var payload = new { contents = new[] { new { parts = new[] { new { text = prompt } } } } };
            var content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

            int maxRetries = 5;
            for (int i = 0; i < maxRetries; i++)
            {
                var response = await _httpClient.PostAsync(url, content);
                var responseString = await response.Content.ReadAsStringAsync();

                if (response.IsSuccessStatusCode)
                {
                    using var doc = JsonDocument.Parse(responseString);
                    return doc.RootElement.GetProperty("candidates")[0].GetProperty("content").GetProperty("parts")[0].GetProperty("text").GetString() ?? "";
                }

                if ((int)response.StatusCode == 429 || (int)response.StatusCode == 503)
                {
                    if (i == maxRetries - 1) 
                        throw new Exception($"API Unavailable or Rate Limit Exceeded after {maxRetries} attempts.");
                    
                    string issue = (int)response.StatusCode == 429 ? "Rate Limit Hit" : "High Demand 503";
                    Console.WriteLine($"[{issue} - Chat] Waiting 35 seconds before retry {i + 1}...");
                    await Task.Delay(35000);
                    continue; 
                }

                Console.WriteLine($"[Google API Error] {responseString}");
                throw new Exception($"Gemini API Error: {responseString}");
            }
            return "";
        }

        public async Task<float[]> GetEmbeddingAsync(string text)
        {
            var url = $"https://generativelanguage.googleapis.com/v1beta/models/gemini-embedding-001:embedContent?key={_googleApiKey}";
            var payload = new { model = "models/gemini-embedding-001", content = new { parts = new[] { new { text = text } } } };
            var content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

            int maxRetries = 5;
            for (int i = 0; i < maxRetries; i++)
            {
                var response = await _httpClient.PostAsync(url, content);
                var responseString = await response.Content.ReadAsStringAsync();
                
                if (response.IsSuccessStatusCode)
                {
                    using var doc = JsonDocument.Parse(responseString);
                    return doc.RootElement.GetProperty("embedding").GetProperty("values").EnumerateArray().Select(v => v.GetSingle()).ToArray();
                }

                if ((int)response.StatusCode == 429 || (int)response.StatusCode == 503)
                {
                    if (i == maxRetries - 1) 
                        throw new Exception($"API Unavailable or Rate Limit Exceeded for Embeddings.");
                    
                    string issue = (int)response.StatusCode == 429 ? "Rate Limit Hit" : "High Demand 503";
                    Console.WriteLine($"[{issue} - Embedding] Waiting 35 seconds before retry {i + 1}...");
                    await Task.Delay(35000);
                    continue;
                }

                Console.WriteLine($"[Google API Error] {responseString}");
                throw new Exception($"Google API Error ({(int)response.StatusCode}): {responseString}");
            }
            return Array.Empty<float>();
        }

        public async Task<List<float[]>> GetEmbeddingsBatchAsync(List<string> texts)
        {
            if (texts == null || !texts.Any()) return new List<float[]>();

            var allEmbeddings = new List<float[]>();
            int maxBatchSize = 15; 
            for (int chunkIndex = 0; chunkIndex < texts.Count; chunkIndex += maxBatchSize)
            {
                var currentChunkBatch = texts.Skip(chunkIndex).Take(maxBatchSize).ToList();

                var url = $"https://generativelanguage.googleapis.com/v1beta/models/gemini-embedding-001:batchEmbedContents?key={_googleApiKey}";

                var requests = currentChunkBatch.Select(text => new
                {
                    model = "models/gemini-embedding-001",
                    content = new { parts = new[] { new { text = text } } }
                }).ToArray();

                var payload = new { requests = requests };
                var content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

                int maxRetries = 5;
                bool success = false;

                for (int i = 0; i < maxRetries; i++)
                {
                    var response = await _httpClient.PostAsync(url, content);
                    var responseString = await response.Content.ReadAsStringAsync();

                    if (response.IsSuccessStatusCode)
                    {
                        using var doc = JsonDocument.Parse(responseString);
                        foreach (var element in doc.RootElement.GetProperty("embeddings").EnumerateArray())
                        {
                            allEmbeddings.Add(element.GetProperty("values").EnumerateArray().Select(v => v.GetSingle()).ToArray());
                        }
                        success = true;
                        break;
                    }

                    if ((int)response.StatusCode == 429 || (int)response.StatusCode == 503)
                    {
                        if (i == maxRetries - 1) 
                            throw new Exception($"API Unavailable or Rate Limit Exceeded for Batch Embeddings.");
                        
                        string issue = (int)response.StatusCode == 429 ? "Rate Limit Hit" : "High Demand 503";
                        Console.WriteLine($"[{issue} - Batch Embedding] Waiting 35 seconds before retry {i + 1}...");
                        await Task.Delay(60000);
                        continue;
                    }

                    Console.WriteLine($"[Google API Error] {responseString}");
                    throw new Exception($"Google API Error ({(int)response.StatusCode}): {responseString}");
                }

                if (!success) break;

                if (chunkIndex + maxBatchSize < texts.Count)
                {
                    Console.WriteLine($"[API Pacing] Sent a batch of 15 successfully. Waiting 5 seconds...");
                    await Task.Delay(5000); 
                }
            }

            return allEmbeddings;
        }
    }
}