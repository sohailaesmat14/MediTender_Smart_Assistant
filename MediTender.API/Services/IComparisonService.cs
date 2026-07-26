using MediTender.API.Models;

namespace MediTender.API.Services
{
    public interface IComparisonService
    {
        Task<List<ComparisonResult>> CompareOfferAsync(List<string> requirements);
    }
}