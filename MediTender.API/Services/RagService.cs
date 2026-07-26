using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Qdrant.Client;
using MediTender.API.Data;
using MediTender.API.Models;

namespace MediTender.API.Services
{
    public class RagService : IRagService
    {
        private readonly QdrantClient _qdrantClient;
        private readonly ApplicationDbContext _dbContext;
        private readonly string _googleApiKey;
        private readonly string _collectionName = "meditender_collection_v2";

        public RagService(QdrantClient qdrantClient, ApplicationDbContext dbContext, IConfiguration config)
        {
            _qdrantClient = qdrantClient;
            _dbContext = dbContext;
            _googleApiKey = config["Gemini:ApiKey"] ?? throw new Exception("Gemini API Key is missing!");
        }

        public async Task<string> AnalyzeOfferAsync(string question)
        {
            var questionEmbedding = await GetEmbeddingAsync(question);

            var searchResults = await _qdrantClient.SearchAsync(
                collectionName: _collectionName,
                vector: questionEmbedding,
                limit: 5
            );

            var contextBuilder = new StringBuilder();
            foreach (var result in searchResults)
            {
                if (result.Payload.TryGetValue("text", out var textValue))
                {
                    contextBuilder.AppendLine(textValue.StringValue);
                    contextBuilder.AppendLine("---");
                }
            }
            var context = contextBuilder.ToString();

            var prompt = $@"
            You are an expert Biomedical Tendering Engineer. Your role is to evaluate company offers.
            Based ONLY on the extracted information from the following documents, answer the question accurately.
            If the answer is not present in the documents, say 'Sorry, there is not enough information in the provided offer.'
            You must support your answer with reasons.

            Extracted Information (Context):
            {context}

            Question:
            {question}
            ";

            var answer = await GenerateChatResponseAsync(prompt);

            var interaction = new TenderInteraction
            {
                Question = question,
                Answer = answer
            };

            _dbContext.TenderInteractions.Add(interaction);
            await _dbContext.SaveChangesAsync();

            return answer;
        }

        private async Task<string> GenerateChatResponseAsync(string prompt)
        {
            using var client = new HttpClient();
            var url = $"https://generativelanguage.googleapis.com/v1beta/models/gemini-3.5-flash:generateContent?key={_googleApiKey}";

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
                throw new Exception($"Google API Error: {responseString}");

            using var doc = JsonDocument.Parse(responseString);
            var generatedAnswer = doc.RootElement
                .GetProperty("candidates")[0]
                .GetProperty("content")
                .GetProperty("parts")[0]
                .GetProperty("text")
                .GetString();

            return generatedAnswer ?? "No answer retrieved.";
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
                throw new Exception($"Google API Error: {responseString}");

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