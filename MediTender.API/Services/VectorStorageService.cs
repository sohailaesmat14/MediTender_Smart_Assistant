using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Qdrant.Client;
using Qdrant.Client.Grpc;

namespace MediTender.API.Services
{
    public class VectorStorageService : IVectorStorageService
    {
        private readonly QdrantClient _qdrantClient;
        private readonly string _googleApiKey;
        private readonly string _collectionName = "meditender_collection_v2";

        public VectorStorageService(QdrantClient qdrantClient, IConfiguration config)
        {
            _qdrantClient = qdrantClient;
            _googleApiKey = config["Gemini:ApiKey"] ?? throw new Exception("Missing API Key");
        }

        public async Task SaveChunksToQdrantAsync(string fileName, string documentType, string vendorName, List<string> chunks)
        {
            var points = new List<PointStruct>();
            ulong idCounter = (ulong)DateTime.UtcNow.Ticks;

            foreach (var chunk in chunks)
            {
                var embedding = await GetEmbeddingAsync(chunk);
                
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

        private async Task<float[]> GetEmbeddingAsync(string text)
        {
            using var client = new HttpClient();
            var url = $"https://generativelanguage.googleapis.com/v1beta/models/gemini-embedding-001:embedContent?key={_googleApiKey}";
            
            var payload = new {
                model = "models/gemini-embedding-001",
                content = new { parts = new[] { new { text = text } } }
            };
            
            var json = JsonSerializer.Serialize(payload);
            var content = new StringContent(json, Encoding.UTF8, "application/json");

            var response = await client.PostAsync(url, content);
            var responseString = await response.Content.ReadAsStringAsync();
            
            if (!response.IsSuccessStatusCode)
                throw new Exception(responseString);

            using var doc = JsonDocument.Parse(responseString);
            return doc.RootElement
                .GetProperty("embedding")
                .GetProperty("values")
                .EnumerateArray()
                .Select(v => v.GetSingle())
                .ToArray();
        }
    }
}