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
        private readonly IFinancialEvaluationService _financialService; 
        private readonly string _collectionName = "meditender_collection_v2";

        public ComparisonService(
            QdrantClient qdrantClient, 
            ApplicationDbContext dbContext, 
            IGeminiService geminiService,
            IFinancialEvaluationService financialService)
            {
            _qdrantClient = qdrantClient;
            _dbContext = dbContext;
            _geminiService = geminiService;
            _financialService = financialService; 
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
                    var guardFilter = new Filter();
                    guardFilter.Must.Add(new Condition { Field = new FieldCondition { Key = "tenderId", Match = new Match { Keyword = tenderId.ToString() } } });
                    guardFilter.Must.Add(new Condition { Field = new FieldCondition { Key = "documentType", Match = new Match { Keyword = "TechnicalOffer" } } });
                    guardFilter.Must.Add(new Condition { Field = new FieldCondition { Key = "vendorName", Match = new Match { Keyword = vendor } } });

                    var guardCheck = await _qdrantClient.SearchAsync(_collectionName, reqEmbeddings.First(), guardFilter, limit: 1);

                    if (guardCheck.Count == 0)
                    {
                        throw new Exception($"[Data Missing] No Technical Offer found for vendor '{vendor}' under Tender ID '{tenderId}'. Database IDs might be out of sync. Please hit Reset System and try again.");
                    }
                    for (int i = 0; i < requirements.Count; i++)
                    {
                        var req = requirements[i];
                        if (i >= reqEmbeddings.Count) break;
                        var reqEmbedding = reqEmbeddings[i]; 
                        
                        var filter = new Filter();
                        filter.Must.Add(new Condition { Field = new FieldCondition { Key = "tenderId", Match = new Match { Keyword = tenderId.ToString() } } });
                        
                        filter.Must.Add(new Condition { Field = new FieldCondition { Key = "documentType", Match = new Match { Keyword = "TechnicalOffer" } } });
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
                    - ""Status"": string (MUST BE EXACTLY ONE OF: ""Met"", ""Partially Met"", or ""Not Met"". If it is missing or not mentioned, you MUST use ""Not Met"")
                    - ""Evidence"": Exact quote from the context supporting the status. If no context exists, return ""No evidence found.""
                    - ""Score"": integer from 0 to 10.
                    ";

                    var aiResponse = await _geminiService.GenerateChatResponseAsync(prompt);
                    
                    int startIndex = aiResponse.IndexOf('[');
                    int endIndex = aiResponse.LastIndexOf(']');
                    if (startIndex != -1 && endIndex != -1 && endIndex > startIndex)
                    {
                        var cleanedJson = aiResponse.Substring(startIndex, endIndex - startIndex + 1);
                        var parsedArray = JsonSerializer.Deserialize<JsonElement>(cleanedJson);
                        int arrayLength = parsedArray.ValueKind == JsonValueKind.Array ? parsedArray.GetArrayLength() : 0;

                        for (int i = 0; i < requirements.Count; i++)
                        {
                            var req = requirements[i];
                            var aiDetail = i < arrayLength ? parsedArray[i] : default;

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
                    else 
                    {
                        throw new Exception("Could not find valid JSON in AI response.");
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

                bool hasFailedMandatory = evaluation.Details.Any(d => d.IsMandatory && (d.Status != "Met" && d.Status != "Partially Met"));
                bool isTechnicallyAccepted = !hasFailedMandatory;
                evaluation.FinalDecision = isTechnicallyAccepted ? "Accepted" : "Rejected";

                _dbContext.OfferEvaluations.Add(evaluation);
                allEvaluations.Add(evaluation);

                await _financialService.EvaluateFinancialOfferAsync(
                    tenderId: tenderId, 
                    vendorName: vendor, 
                    isTechnicallyAccepted: isTechnicallyAccepted, 
                    technicalScore: evaluation.TotalScore
                );
                var finOffer = await _financialService.EvaluateFinancialOfferAsync(
                    tenderId: tenderId, 
                    vendorName: vendor, 
                    isTechnicallyAccepted: isTechnicallyAccepted, 
                    technicalScore: evaluation.TotalScore
                );
                
                evaluation.TotalPrice = finOffer.TotalPrice; // <-- السطر الجديد

                _dbContext.OfferEvaluations.Add(evaluation);
                allEvaluations.Add(evaluation);
            }

            await _dbContext.SaveChangesAsync();
            return allEvaluations;
        }
    }
}