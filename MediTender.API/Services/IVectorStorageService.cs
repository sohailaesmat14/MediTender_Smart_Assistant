namespace MediTender.API.Services
{
    public interface IVectorStorageService
    {
        Task SaveChunksToQdrantAsync(string documentName, List<string> chunks);
    }
}