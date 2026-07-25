#pragma warning disable CS0618

using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.Embeddings;
using Qdrant.Client;
using Qdrant.Client.Grpc;

namespace MediTender.API.Services
{
    public class VectorStorageService : IVectorStorageService
    {
        private readonly QdrantClient _qdrantClient;
        private readonly ITextEmbeddingGenerationService _embeddingGeneration;
        private readonly string _collectionName = "meditender_collection";

        public VectorStorageService(QdrantClient qdrantClient, Kernel kernel)
        {
            _qdrantClient = qdrantClient;
            _embeddingGeneration = kernel.GetRequiredService<ITextEmbeddingGenerationService>();
        }

        public async Task SaveChunksToQdrantAsync(string documentName, List<string> chunks)
        {
            var collections = await _qdrantClient.ListCollectionsAsync();
            if (!collections.Contains(_collectionName))
            {
                await _qdrantClient.CreateCollectionAsync(_collectionName, new VectorParams { Size = 768, Distance = Distance.Cosine });
            }

            var embeddings = await _embeddingGeneration.GenerateEmbeddingsAsync(chunks);

            var points = new List<PointStruct>();
            for (int i = 0; i < chunks.Count; i++)
            {
                var id = (ulong)Guid.NewGuid().GetHashCode(); 
                
                var point = new PointStruct
                {
                    Id = id,
                    Vectors = embeddings[i].ToArray(),
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
    }
}