using System.Text.Json;
using Qdrant.Client;
using Qdrant.Client.Grpc;
using MediTender.API.Models;
using MediTender.API.Data;

namespace MediTender.API.Services
{
    public class FinancialEvaluationService : IFinancialEvaluationService
    {
        private readonly QdrantClient _qdrantClient;
        private readonly ApplicationDbContext _dbContext;
        private readonly IGeminiService _geminiService;
        private readonly string _collectionName = "meditender_collection_v2";

        public FinancialEvaluationService(QdrantClient qdrantClient, ApplicationDbContext dbContext, IGeminiService geminiService)
        {
            _qdrantClient = qdrantClient;
            _dbContext = dbContext;
            _geminiService = geminiService;
        }

        public async Task<VendorOffer> EvaluateFinancialOfferAsync(int tenderId, string vendorName, bool isTechnicallyAccepted, decimal technicalScore)
        {
            var offer = new VendorOffer
            {
                TenderId = tenderId,
                CompanyName = vendorName,
                EvaluationScore = technicalScore,
                IsAccepted = isTechnicallyAccepted,
                TotalPrice = 0,
                AiRejectionReason = string.Empty
            };

            if (!isTechnicallyAccepted)
            {
                offer.AiRejectionReason = "Rejected technically. Financial envelope not opened.";
                _dbContext.VendorOffers.Add(offer);
                await _dbContext.SaveChangesAsync();
                return offer;
            }

            try
            {
                var searchVector = await _geminiService.GetEmbeddingAsync("total price, total cost, grand total, currency, warranty period, payment terms");

                var filter = new Filter();
                filter.Must.Add(new Condition { Field = new FieldCondition { Key = "documentType", Match = new Match { Keyword = "FinancialOffer" } } });
                filter.Must.Add(new Condition { Field = new FieldCondition { Key = "vendorName", Match = new Match { Keyword = vendorName } } });

                var searchResults = await _qdrantClient.SearchAsync(_collectionName, searchVector, filter, limit: 7);

                var contextBuilder = new System.Text.StringBuilder();
                foreach (var result in searchResults)
                {
                    if (result.Payload.TryGetValue("text", out var textValue))
                        contextBuilder.AppendLine(textValue.StringValue);
                }

                var prompt = $@"
                You are a Procurement Financial Analyst. Extract the financial details from the following context for vendor '{vendorName}'.
                
                Context:
                {contextBuilder}
                
                Return ONLY a valid JSON object with the following structure. Do not include markdown:
                {{
                    ""TotalPrice"": 0.0,
                    ""Notes"": ""Any conditions like warranty or delivery time""
                }}
                Make sure TotalPrice is a number. If not found, return 0.
                ";

                var aiResponse = await _geminiService.GenerateChatResponseAsync(prompt);
                
                int startIndex = aiResponse.IndexOf('{');
                int endIndex = aiResponse.LastIndexOf('}');
                
                if (startIndex != -1 && endIndex != -1 && endIndex > startIndex)
                {
                    var cleanedJson = aiResponse.Substring(startIndex, endIndex - startIndex + 1);
                    using var doc = JsonDocument.Parse(cleanedJson);
                    
                    offer.TotalPrice = doc.RootElement.GetProperty("TotalPrice").GetDecimal();
                    string notes = doc.RootElement.GetProperty("Notes").GetString() ?? "";
                    
                    if (offer.TotalPrice <= 0)
                    {
                        offer.AiRejectionReason = "Warning: Could not detect a valid total price from the financial document.";
                    }
                }
            }
            catch (Exception ex)
            {
                offer.AiRejectionReason = $"Error processing financial document: {ex.Message}";
            }

            _dbContext.VendorOffers.Add(offer);
            await _dbContext.SaveChangesAsync();

            return offer;
        }
    }
}