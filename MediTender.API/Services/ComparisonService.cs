using System.Text;
using System.Text.Json;
using Qdrant.Client;
using Qdrant.Client.Grpc;
using MediTender.API.Models;
using MediTender.API.Data;

namespace MediTender.API.Services
{
    public class ComparisonService : IComparisonService
    {
        private readonly QdrantClient _qdrantClient;
        private readonly ApplicationDbContext _dbContext;
        private readonly IGeminiService _geminiService;
        private readonly string _collectionName = "meditender_collection_v2";

        public ComparisonService(QdrantClient qdrantClient, ApplicationDbContext dbContext, IGeminiService geminiService)
        {
            _qdrantClient = qdrantClient;
            _dbContext = dbContext;
            _geminiService = geminiService;
        }

        public async Task<List<OfferEvaluation>> CompareVendorsAsync(int tenderId, List<Standard> requirements, List<string> vendorNames)
        {
            var allEvaluations = new List<OfferEvaluation>();

            var reqTexts = requirements.Select(r => r.RequirementText).ToList();
            var reqEmbeddings = await _geminiService.GetEmbeddingsBatchAsync(reqTexts);

            foreach (var vendor in vendorNames)
            {
                var evaluation = new OfferEvaluation
                {
                    TenderId = tenderId,
                    VendorName = vendor,
                    EvaluationDate = DateTime.UtcNow,
                    TotalScore = 0,
                    FinalDecision = "Pending",
                    Details = new List<EvaluationDetail>()
                };

                try
                {
                    var contextBuilder = new StringBuilder();
                    
                    for (int i = 0; i < requirements.Count; i++)
                    {
                        var req = requirements[i];
                        if (i >= reqEmbeddings.Count) break;
                        var reqEmbedding = reqEmbeddings[i]; 
                        
                        var filter = new Filter();
                        filter.Must.Add(new Condition { Field = new FieldCondition { Key = "documentType", Match = new Match { Keyword = "Offer" } } });
                        filter.Must.Add(new Condition { Field = new FieldCondition { Key = "vendorName", Match = new Match { Keyword = vendor } } });

                        var searchResults = await _qdrantClient.SearchAsync(_collectionName, reqEmbedding, filter, limit: 5);

                        foreach (var result in searchResults)
                        {
                            if (result.Payload.TryGetValue("text", out var textValue))
                                contextBuilder.AppendLine(textValue.StringValue);
                        }
                    }

                    var reqsJson = JsonSerializer.Serialize(requirements.Select(r => new { r.RequirementText, r.IsMandatory }));

                    var prompt = $@"
                    You are a Biomedical Procurement Expert. Evaluate the following JSON list of requirements against the provided document context from vendor '{vendor}'.
                    
                    Requirements List:
                    {reqsJson}
                    
                    Context:
                    '{contextBuilder}'
                    
                    Return ONLY a valid JSON array of objects. Each object must exactly match a requirement and have the following keys:
                    - ""RequirementText"": string (exact match from the provided list)
                    - ""Status"": ""Met"", ""Partially Met"", or ""Not Met""
                    - ""Evidence"": Exact quote from the context supporting the status. If no context exists, return ""No evidence found.""
                    - ""Score"": integer from 0 to 10.
                    ";

                    var aiResponse = await _geminiService.GenerateChatResponseAsync(prompt);
                    var cleanedJson = aiResponse.Replace("```json", "").Replace("```", "").Trim();
                    
                    var parsedArray = JsonSerializer.Deserialize<JsonElement>(cleanedJson);

                    foreach (var req in requirements)
                    {
                        var aiDetail = parsedArray.EnumerateArray()
                            .FirstOrDefault(x => x.GetProperty("RequirementText").GetString() == req.RequirementText);

                        var detail = new EvaluationDetail
                        {
                            Requirement = req.RequirementText,
                            IsMandatory = req.IsMandatory, 
                            Status = aiDetail.ValueKind != JsonValueKind.Undefined ? aiDetail.GetProperty("Status").GetString() ?? "Not Met" : "Error",
                            Evidence = aiDetail.ValueKind != JsonValueKind.Undefined ? aiDetail.GetProperty("Evidence").GetString() ?? "" : "AI missed this requirement.",
                            Score = aiDetail.ValueKind != JsonValueKind.Undefined ? aiDetail.GetProperty("Score").GetInt32() : 0
                        };

                        evaluation.Details.Add(detail);
                        evaluation.TotalScore += detail.Score;
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[Batched AI Error for {vendor}] {ex.Message}");
                    foreach(var req in requirements)
                    {
                        evaluation.Details.Add(new EvaluationDetail
                        {
                            Requirement = req.RequirementText,
                            IsMandatory = req.IsMandatory,
                            Status = "Error",
                            Evidence = $"System Error: {ex.Message}", 
                            Score = 0
                        });
                    }
                }

                bool hasFailedMandatory = evaluation.Details.Any(d => d.IsMandatory && (d.Status == "Not Met" || d.Status == "Error"));
                evaluation.FinalDecision = hasFailedMandatory ? "Rejected" : "Accepted";

                _dbContext.OfferEvaluations.Add(evaluation);
                allEvaluations.Add(evaluation);
            }

            await _dbContext.SaveChangesAsync();
            return allEvaluations;
        }
    }
}