using System;
using System.Collections.Generic;
using System.Linq; 
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Qdrant.Client;
using Qdrant.Client.Grpc;

namespace MediTender.API.Services
{
    public class VectorStorageService : IVectorStorageService 
    {
        private readonly QdrantClient _qdrantClient;
        private readonly IGeminiService _geminiService; 
        private readonly ILogger<VectorStorageService> _logger;
        private readonly string _collectionName = "meditender_collection_v2";

        public VectorStorageService(QdrantClient qdrantClient, IGeminiService geminiService, ILogger<VectorStorageService> logger)
        {
            _qdrantClient = qdrantClient;
            _geminiService = geminiService;
            _logger = logger;
        }

        public async Task SaveChunksToQdrantAsync(string fileName, string documentType, string vendorName, List<string> chunks, int tenderId)
        {

            var allEmbeddings = new List<float[]>();
            int batchSize = 100;
            
            for (int i = 0; i < chunks.Count; i += batchSize)
            {
                var currentBatch = chunks.Skip(i).Take(batchSize).ToList();
                var batchEmbeddings = await _geminiService.GetEmbeddingsBatchAsync(currentBatch);
                allEmbeddings.AddRange(batchEmbeddings);
                await Task.Delay(1000); 
            }

            var points = new List<PointStruct>();

            for (int i = 0; i < chunks.Count; i++)
            {
                var chunk = chunks[i];
                if (i >= allEmbeddings.Count) break; 
                var embedding = allEmbeddings[i]; 
                
                var payload = new Dictionary<string, Value>
                {
                    { "fileName", new Value { StringValue = fileName } },
                    { "text", new Value { StringValue = chunk } },
                    { "documentType", new Value { StringValue = documentType } },
                    { "vendorName", new Value { StringValue = string.IsNullOrWhiteSpace(vendorName) ? "None" : vendorName } },
                    { "tenderId", new Value { StringValue = tenderId.ToString() } } 
                };

                points.Add(new PointStruct
                {
                    Id = new PointId { Uuid = Guid.NewGuid().ToString() },
                    Vectors = embedding,
                    Payload = { payload }
                });
            }

            int upsertBatchSize = 100;
            for (int i = 0; i < points.Count; i += upsertBatchSize)
            {
                var pointBatch = points.Skip(i).Take(upsertBatchSize).ToList();
                await _qdrantClient.UpsertAsync(_collectionName, pointBatch);
            }
        }
        public async Task DeleteExistingDocumentAsync(int tenderId, string documentType, string vendorName)
        {
            var filter = new Filter();
            filter.Must.Add(new Condition { Field = new FieldCondition { Key = "tenderId", Match = new Match { Keyword = tenderId.ToString() } } });
            filter.Must.Add(new Condition { Field = new FieldCondition { Key = "documentType", Match = new Match { Keyword = documentType } } });
            
            if (!string.IsNullOrWhiteSpace(vendorName))
            {
                filter.Must.Add(new Condition { Field = new FieldCondition { Key = "vendorName", Match = new Match { Keyword = vendorName } } });
            }

            try
            {
                await _qdrantClient.DeleteAsync(_collectionName, filter);
                _logger.LogInformation($"Deleted old chunks for Tender: {tenderId}, Type: {documentType}, Vendor: {vendorName}");
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to delete existing document chunks.");
            }
        }
    }
}