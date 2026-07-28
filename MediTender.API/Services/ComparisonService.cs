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

                foreach (var req in requirements)
                {
                    try 
                    {
                       
                        var reqEmbedding = await _geminiService.GetEmbeddingAsync(req.RequirementText); 
                        
                        var filter = new Filter();
                        filter.Must.Add(new Condition { Field = new FieldCondition { Key = "documentType", Match = new Match { Keyword = "Offer" } } });
                        filter.Must.Add(new Condition { Field = new FieldCondition { Key = "vendorName", Match = new Match { Keyword = vendor } } });

                        var searchResults = await _qdrantClient.SearchAsync(_collectionName, reqEmbedding, filter, limit: 3);

                        var contextBuilder = new StringBuilder();
                        foreach (var result in searchResults)
                        {
                            if (result.Payload.TryGetValue("text", out var textValue))
                                contextBuilder.AppendLine(textValue.StringValue);
                        }
                        
                        var prompt = $@"
                        Evaluate the following requirement against the provided document context from vendor '{vendor}'.
                        Requirement: '{req.RequirementText}'
                        Context: '{contextBuilder}'
                        
                        Return ONLY a valid JSON object with the exact following keys:
                        - status: 'Met', 'Partially Met', or 'Not Met'
                        - evidence: Exact quote from the context supporting the status. If no context exists, return 'No evidence found.'
                        - score: integer from 0 to 10.
                        ";

                        
                        var aiResponse = await _geminiService.GenerateChatResponseAsync(prompt);
                        var cleanedJson = aiResponse.Replace("```json", "").Replace("```", "").Trim();
                        
                        var parsedResponse = JsonSerializer.Deserialize<JsonElement>(cleanedJson);

                        var detail = new EvaluationDetail
                        {
                            Requirement = req.RequirementText,
                            IsMandatory = req.IsMandatory, 
                            Status = parsedResponse.GetProperty("status").GetString() ?? "Not Met",
                            Evidence = parsedResponse.GetProperty("evidence").GetString() ?? "",
                            Score = parsedResponse.GetProperty("score").GetInt32()
                        };

                        evaluation.Details.Add(detail);
                        evaluation.TotalScore += detail.Score;
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[AI Error] {ex.Message}");
                        evaluation.Details.Add(new EvaluationDetail
                        {
                            Requirement = req.RequirementText,
                            IsMandatory = req.IsMandatory,
                            Status = "Error",
                            Evidence = "System Error: AI failed to analyze this requirement.",
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