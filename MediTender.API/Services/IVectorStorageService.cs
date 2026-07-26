namespace MediTender.API.Services
{
    public interface IVectorStorageService
    {
        Task SaveChunksToQdrantAsync(string fileName, string documentType, string vendorName, List<string> chunks);
    }
}