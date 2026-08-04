using System.Text;
using System.Text.Json;
using Qdrant.Client;
using Qdrant.Client.Grpc;
using MediTender.API.Models;

namespace MediTender.API.Services
{
    public class StandardExtractionService : IStandardExtractionService
    {
        private readonly QdrantClient _qdrantClient;
        private readonly IGeminiService _geminiService;  
        private readonly string _collectionName = "meditender_collection_v2";

        public StandardExtractionService(QdrantClient qdrantClient, IGeminiService geminiService)
        {
            _qdrantClient = qdrantClient;
            _geminiService = geminiService;
        }

        public async Task<List<Standard>> ExtractRequirementsAsync(string fileName)
        {
            
            var searchVector = await _geminiService.GetEmbeddingAsync("mandatory technical specifications, requirements, physical characteristics, performance parameters");

            var filter = new Filter();
            filter.Must.Add(new Condition { Field = new FieldCondition { Key = "fileName", Match = new Match { Keyword = fileName } } });

            var searchResults = await _qdrantClient.SearchAsync(
            collectionName: _collectionName,
            vector: searchVector,
            limit: 5);

            var contextBuilder = new StringBuilder();
            foreach (var result in searchResults)
            {
                if (result.Payload.TryGetValue("text", out var textValue))
                    contextBuilder.AppendLine(textValue.StringValue);
            }

            var context = contextBuilder.ToString();
            if (string.IsNullOrWhiteSpace(context))
                throw new Exception("No context found in the database for this file.");

            var prompt = $@"
            You are a Biomedical Procurement Expert. Extract the technical specifications from the following text.
            For each requirement, determine if it is strictly mandatory (must-have) or optional/preferred.
            Return ONLY a valid JSON array of objects. Each object must have:
            - ""RequirementText"": string (the specification)
            - ""IsMandatory"": boolean (true if mandatory, false if optional)
            Do not include any markdown formatting.

            Context:
            {context}
            ";

            
            var aiResponse = await _geminiService.GenerateChatResponseAsync(prompt);
            var cleanedJson = aiResponse.Replace("```json", "").Replace("```", "").Trim();
            
            try
            {
                var requirements = JsonSerializer.Deserialize<List<Standard>>(cleanedJson, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                return requirements ?? new List<Standard>();
            }
            catch
            {
                throw new Exception("AI returned invalid JSON format.");
            }
        }
    }
}