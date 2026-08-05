namespace MediTender.API.Services
{
    public interface IVectorStorageService
    {
        Task DeleteExistingDocumentAsync(int tenderId, string documentType, string vendorName);
        Task SaveChunksToQdrantAsync(string fileName, string documentType, string vendorName, List<string> chunks, int tenderId);
    }
}