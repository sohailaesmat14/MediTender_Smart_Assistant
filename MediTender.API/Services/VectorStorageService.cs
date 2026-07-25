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
            _googleApiKey = config["Gemini:ApiKey"] ?? throw new Exception("Gemini API Key is missing!");
        }

        public async Task SaveChunksToQdrantAsync(string documentName, List<string> chunks)
        {
            var collections = await _qdrantClient.ListCollectionsAsync();
            if (!collections.Contains(_collectionName))
            {
                await _qdrantClient.CreateCollectionAsync(_collectionName, new VectorParams { Size = 3072, Distance = Distance.Cosine });
            }

            var embeddings = await GetEmbeddingsDirectlyAsync(chunks);

            var points = new List<PointStruct>();
            for (int i = 0; i < chunks.Count; i++)
            {
                var id = (ulong)Guid.NewGuid().GetHashCode(); 
                
                var point = new PointStruct
                {
                    Id = id,
                    Vectors = embeddings[i], 
                    Payload = 
                    {
                        ["documentName"] = documentName,
                        ["text"] = chunks[i] 
                    }
                };
                points.Add(point);
            }

            await _qdrantClient.UpsertAsync(_collectionName, points);
        }

        private async Task<List<float[]>> GetEmbeddingsDirectlyAsync(List<string> chunks)
        {
            using var client = new HttpClient();
            var url = $"https://generativelanguage.googleapis.com/v1beta/models/gemini-embedding-001:embedContent?key={_googleApiKey}";
            var embeddingsList = new List<float[]>();

            foreach (var chunk in chunks)
            {
                var payload = new {
                    model = "models/gemini-embedding-001",
                    content = new { parts = new[] { new { text = chunk } } }
                };
                
                var json = JsonSerializer.Serialize(payload);
                var content = new StringContent(json, Encoding.UTF8, "application/json");

                var response = await client.PostAsync(url, content);
                var responseString = await response.Content.ReadAsStringAsync();
                
                if (!response.IsSuccessStatusCode)
                {
                    throw new Exception($"Google API Error: {responseString}");
                }

                using var doc = JsonDocument.Parse(responseString);
                var values = doc.RootElement
                    .GetProperty("embedding")
                    .GetProperty("values")
                    .EnumerateArray()
                    .Select(v => v.GetSingle())
                    .ToArray();
                    
                embeddingsList.Add(values);
            }
            
            return embeddingsList;
        }
    }
}