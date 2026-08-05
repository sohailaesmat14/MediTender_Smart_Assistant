using System.Text;
using Qdrant.Client;
using Qdrant.Client.Grpc;
using MediTender.API.Data;
using MediTender.API.Models;

namespace MediTender.API.Services
{
    public class RagService : IRagService
    {
        private readonly QdrantClient _qdrantClient;
        private readonly ApplicationDbContext _dbContext;
        private readonly IGeminiService _geminiService;
        private readonly string _collectionName = "meditender_collection_v2";

        public RagService(QdrantClient qdrantClient, ApplicationDbContext dbContext, IGeminiService geminiService)
        {
            _qdrantClient = qdrantClient;
            _dbContext = dbContext;
            _geminiService = geminiService;
        }

        public async Task<string> AnalyzeOfferAsync(string question, int tenderId, string vendorName)
        {
            var questionEmbedding = await _geminiService.GetEmbeddingAsync(question);

            var filter = new Filter();
            filter.Must.Add(new Condition { Field = new FieldCondition { Key = "tenderId", Match = new Match { Keyword = tenderId.ToString() } } });
            
            if (!string.IsNullOrWhiteSpace(vendorName))
            {
                filter.Must.Add(new Condition { Field = new FieldCondition { Key = "vendorName", Match = new Match { Keyword = vendorName } } });
            }

            var searchResults = await _qdrantClient.SearchAsync(
                collectionName: _collectionName,
                vector: questionEmbedding,
                filter: filter,
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

            var answer = await _geminiService.GenerateChatResponseAsync(prompt);

            var interaction = new TenderInteraction
            {
                Question = question,
                Answer = answer
            };

            _dbContext.TenderInteractions.Add(interaction);
            await _dbContext.SaveChangesAsync();

            return answer;
        }
    }
}