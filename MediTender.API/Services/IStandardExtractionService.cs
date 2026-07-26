namespace MediTender.API.Services
{
    public interface IStandardExtractionService
    {
        Task<List<string>> ExtractRequirementsAsync(string fileName);
    }
}