using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Qdrant.Client;
using Qdrant.Client.Grpc;

namespace MediTender.API.Services
{
    public class VectorStorageService : IVectorStorageService
    {
        private readonly QdrantClient _qdrantClient;
        private readonly IGeminiService _geminiService; 
        private readonly string _collectionName = "meditender_collection_v2";

        public VectorStorageService(QdrantClient qdrantClient, IGeminiService geminiService)
        {
            _qdrantClient = qdrantClient;
            _geminiService = geminiService;
        }

        public async Task SaveChunksToQdrantAsync(string fileName, string documentType, string vendorName, List<string> chunks)
        {
            try
            {
                await _qdrantClient.CreatePayloadIndexAsync(_collectionName, "fileName", PayloadSchemaType.Keyword);
                await _qdrantClient.CreatePayloadIndexAsync(_collectionName, "documentType", PayloadSchemaType.Keyword);
                await _qdrantClient.CreatePayloadIndexAsync(_collectionName, "vendorName", PayloadSchemaType.Keyword);
            }
            catch
            {
                
            }

            var points = new List<PointStruct>();
            ulong idCounter = (ulong)DateTime.UtcNow.Ticks;

            var embeddings = await _geminiService.GetEmbeddingsBatchAsync(chunks);

            for (int i = 0; i < chunks.Count; i++)
            {
                var chunk = chunks[i];
                if (i >= embeddings.Count) break; 
                var embedding = embeddings[i];
                
                var payload = new Dictionary<string, Value>
                {
                    { "fileName", new Value { StringValue = fileName } },
                    { "text", new Value { StringValue = chunk } },
                    { "documentType", new Value { StringValue = documentType } },
                    { "vendorName", new Value { StringValue = string.IsNullOrWhiteSpace(vendorName) ? "None" : vendorName } }
                };

                points.Add(new PointStruct
                {
                    Id = new PointId { Num = idCounter++ },
                    Vectors = embedding,
                    Payload = { payload }
                });
            }

            await _qdrantClient.UpsertAsync(_collectionName, points);
        }
    }
}