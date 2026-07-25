using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.SemanticKernel;
using Qdrant.Client;

namespace MediTender.API.Services
{
    public class RagService : IRagService
    {
        private readonly QdrantClient _qdrantClient;
        private readonly Kernel _kernel;
        private readonly string _googleApiKey;
        private readonly string _collectionName = "meditender_collection_v2";

        public RagService(QdrantClient qdrantClient, Kernel kernel, IConfiguration config)
        {
            _qdrantClient = qdrantClient;
            _kernel = kernel;
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
            You are an expert Biomedical Tendering Engineer. Your job is to evaluate company offers.
            Based ONLY on the extracted information from the documents below, answer the question accurately.
            If the answer is not found in the documents, say 'Sorry, there is not enough information in the provided offer.'
            You must support your answer with reasons.

            Extracted Information (Context):
            {context}

            Question:
            {question}
            ";

            var response = await _kernel.InvokePromptAsync(prompt);
            return response.GetValue<string>() ?? "No answer retrieved.";
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