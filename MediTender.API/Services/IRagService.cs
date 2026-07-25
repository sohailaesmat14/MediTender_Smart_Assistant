namespace MediTender.API.Services
{
    public interface IRagService
    {
        Task<string> AnalyzeOfferAsync(string question);
    }
}