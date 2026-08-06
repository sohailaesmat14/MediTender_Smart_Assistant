using System.Text;
using System.Text.Json;
using Qdrant.Client;
using Qdrant.Client.Grpc;
using MediTender.API.Models;
using MediTender.API.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace MediTender.API.Services
{   
    public class ComparisonService : IComparisonService
    {
        private readonly ILogger<ComparisonService> _logger;
        private readonly QdrantClient _qdrantClient;
        private readonly ApplicationDbContext _dbContext;
        private readonly IGeminiService _geminiService;
        private readonly IFinancialEvaluationService _financialService; 
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly string _collectionName = "meditender_collection_v2";

        public ComparisonService(
            QdrantClient qdrantClient, 
            IGeminiService geminiService,
            IServiceScopeFactory scopeFactory, 
            ILogger<ComparisonService> logger)
        {
            _logger = logger;
            _qdrantClient = qdrantClient;
            _geminiService = geminiService;
            _scopeFactory = scopeFactory;
        }

        public async Task<List<OfferEvaluation>> CompareVendorsAsync(int tenderId, List<Standard> requirements, List<string> vendorNames, CancellationToken cancellationToken = default)
        {
            var reqTexts = requirements.Select(r => r.RequirementText).ToList();
            var reqEmbeddings = await _geminiService.GetEmbeddingsBatchAsync(reqTexts, cancellationToken);

            using (var setupScope = _scopeFactory.CreateScope())
            {
                var dbContext = setupScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                var existingTender = await dbContext.Tenders.FindAsync(new object[] { tenderId }, cancellationToken);
                if (existingTender == null)
                {
                    dbContext.Tenders.Add(new Tender { Id = tenderId, Description = "Auto-created during evaluation process" });
                    await dbContext.SaveChangesAsync(cancellationToken);
                }
            }

            var evaluationTasks = vendorNames.Select(async vendor =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                
                using var scope = _scopeFactory.CreateScope();
                var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                var financialService = scope.ServiceProvider.GetRequiredService<IFinancialEvaluationService>();

                var oldEvaluations = await dbContext.OfferEvaluations
                    .Include(e => e.Details)
                    .Where(e => e.TenderId == tenderId && e.VendorName == vendor)
                    .ToListAsync(cancellationToken);

                if (oldEvaluations.Any())
                {
                    dbContext.EvaluationDetails.RemoveRange(oldEvaluations.SelectMany(e => e.Details));
                    dbContext.OfferEvaluations.RemoveRange(oldEvaluations);
                }

                var oldFinancialOffers = await dbContext.VendorOffers
                    .Where(v => v.TenderId == tenderId && v.CompanyName == vendor)
                    .ToListAsync(cancellationToken);

                if (oldFinancialOffers.Any()) 
                    dbContext.VendorOffers.RemoveRange(oldFinancialOffers);
                    
                await dbContext.SaveChangesAsync(cancellationToken);
                    
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

                    var guardCheck = await _qdrantClient.SearchAsync(_collectionName, reqEmbeddings.First(), guardFilter, limit: 1, cancellationToken: cancellationToken);
                    
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

                        var searchResults = await _qdrantClient.SearchAsync(_collectionName, reqEmbedding, filter, limit: 8, cancellationToken: cancellationToken);

                        foreach (var result in searchResults)
                        {
                            if (result.Payload.TryGetValue("text", out var textValue))
                                contextBuilder.AppendLine(textValue.StringValue);
                        }
                    }

                    var reqsJson = JsonSerializer.Serialize(requirements.Select(r => new { r.Id, r.RequirementText, r.IsMandatory }));

                    var prompt = $@"
                    You are a Biomedical Procurement Expert. Evaluate the following JSON list of requirements against the provided document context from vendor '{vendor}'.
                    Requirements List:
                    {reqsJson}

                    Context:
                    '{contextBuilder}'

                    Return ONLY a valid JSON array of objects. Each object must exactly match a requirement and have the following keys:
                    
                    - ""Id"": integer (MUST exactly match the Id from the provided list)
                    - ""Status"": string (MUST BE EXACTLY ONE OF: ""Met"", ""Partially Met"", ""Not Met"", or ""Not Mentioned"". If there is no evidence or the context does not specify, use ""Not Mentioned"")
                    - ""Evidence"": Exact quote from the context supporting the status. If no context exists, return ""No evidence found.""
                    - ""Score"": integer from 0 to 10.";

                    var aiResponse = await _geminiService.GenerateChatResponseAsync(prompt, cancellationToken);
                    
                    int startIndex = aiResponse.IndexOf('[');
                    int endIndex = aiResponse.LastIndexOf(']');
                    if (startIndex != -1 && endIndex != -1 && endIndex > startIndex)
                    {
                        var cleanedJson = aiResponse.Substring(startIndex, endIndex - startIndex + 1);
                        var parsedArray = JsonSerializer.Deserialize<JsonElement>(cleanedJson);

                        for (int i = 0; i < requirements.Count; i++)
                        {
                            var req = requirements[i];
                            JsonElement aiDetail = default;
                            bool matchFound = false;

                            if (parsedArray.ValueKind == JsonValueKind.Array)
                            {
                                foreach (var item in parsedArray.EnumerateArray())
                                {
                                    if (item.TryGetProperty("Id", out var idProp) && idProp.ValueKind == JsonValueKind.Number)
                                    {
                                        if (idProp.GetInt32() == req.Id)
                                        {
                                            aiDetail = item;
                                            matchFound = true;
                                            break;
                                        }
                                    }
                                }
                            }

                            int baseScore = 0;
                            string status = "Not Mentioned";
                            string evidence = "AI missed this requirement in the evaluation.";

                            if (matchFound)
                            {
                                if (aiDetail.TryGetProperty("Score", out var scoreProp) && scoreProp.ValueKind == JsonValueKind.Number)
                                    baseScore = scoreProp.GetInt32();

                                if (aiDetail.TryGetProperty("Status", out var statusProp))
                                    status = statusProp.GetString() ?? "Not Met";
                                else
                                    status = "Not Met"; 

                                if (aiDetail.TryGetProperty("Evidence", out var evidenceProp))
                                    evidence = evidenceProp.GetString() ?? "No evidence found.";
                            }

                            int weight = req.IsMandatory ? 2 : 1; 
                            int weightedScore = baseScore * weight;

                            var detail = new EvaluationDetail
                            {
                                Requirement = req.RequirementText,
                                IsMandatory = req.IsMandatory, 
                                Status = status,
                                Evidence = evidence,
                                Score = weightedScore 
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
                    _logger.LogError(ex, "Batched AI Error for vendor {VendorName}", vendor);
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
                bool hasPartialOrMissingMandatory = evaluation.Details.Any(d => d.IsMandatory && (d.Status == "Partially Met" || d.Status == "Not Mentioned"));

                if (hasFailedMandatory)
                    evaluation.FinalDecision = "Recommended for Rejection";
                else if (hasPartialOrMissingMandatory)
                    evaluation.FinalDecision = "Pending Manual Review";
                else
                    evaluation.FinalDecision = "Recommended for Acceptance";

                bool openFinancialEnvelope = evaluation.FinalDecision == "Recommended for Acceptance" || evaluation.FinalDecision == "Pending Manual Review";
                
                var finOffer = await financialService.EvaluateFinancialOfferAsync(
                    tenderId: tenderId, 
                    vendorName: vendor, 
                    isTechnicallyAccepted: openFinancialEnvelope,
                    technicalScore: evaluation.TotalScore
                );
                
                evaluation.TotalPrice = finOffer.TotalPrice; 

                dbContext.OfferEvaluations.Add(evaluation);
                await dbContext.SaveChangesAsync(cancellationToken);

                return evaluation;
            });

            var allEvaluations = (await Task.WhenAll(evaluationTasks)).ToList();
            return allEvaluations;
        }
    }
}