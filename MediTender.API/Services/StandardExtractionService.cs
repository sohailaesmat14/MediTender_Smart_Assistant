using System.Text;
using System.Text.Json;
using Qdrant.Client;
using Qdrant.Client.Grpc;
using MediTender.API.Models;
using MediTender.API.Data; 

namespace MediTender.API.Services
{
    public class StandardExtractionService : IStandardExtractionService
    {
        private readonly QdrantClient _qdrantClient;
        private readonly IGeminiService _geminiService;  
        private readonly ApplicationDbContext _dbContext; 
        private readonly string _collectionName = "meditender_collection_v2";

        public StandardExtractionService(QdrantClient qdrantClient, IGeminiService geminiService, ApplicationDbContext dbContext)
        {
            _qdrantClient = qdrantClient;
            _geminiService = geminiService;
            _dbContext = dbContext;
        }

        public async Task<List<Standard>> ExtractRequirementsAsync(string fileName, int tenderId)
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
            For each requirement, extract a short category or item name, a brief description, and the exact specification.
            Determine if it is strictly mandatory (must-have) or optional/preferred.
            
            Set 'IsMandatory' to true ONLY IF the requirement explicitly contains words like 'Mandatory', 'Must', 'Shall', or 'Required'. If it contains words like 'Preferable', 'Optional', or has no strict enforcing word, you MUST set 'IsMandatory' to false.

            Return ONLY a valid JSON array of objects. Each object must exactly match the C# model properties and have the following keys:
            - ""ItemName"": string (a short title or category for the requirement, e.g., 'Display', 'Battery', 'Dimensions', 'Power Supply')
            - ""Description"": string (a brief summary of what this specification entails)
            - ""RequirementText"": string (the exact technical specification from the context)
            - ""IsMandatory"": boolean (true if mandatory, false if optional)
            
            Do not include any markdown formatting or json code blocks.

            Context:
            {context}
            ";
            
            var aiResponse = await _geminiService.GenerateChatResponseAsync(prompt);
            var cleanedJson = aiResponse.Replace("```json", "").Replace("```", "").Trim();
            
            try
            {
                var requirements = JsonSerializer.Deserialize<List<Standard>>(cleanedJson, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                
                if (requirements != null && requirements.Any())
                {
                    var oldStandards = _dbContext.Standards.Where(s => s.TenderId == tenderId);
                    _dbContext.Standards.RemoveRange(oldStandards);

                    foreach (var req in requirements)
                    {
                        req.TenderId = tenderId;
                        _dbContext.Standards.Add(req);
                    }
                    await _dbContext.SaveChangesAsync();
                }

                return requirements ?? new List<Standard>();
            }
            catch
            {
                throw new Exception("AI returned invalid JSON format.");
            }
        }
    }
}