using MediTender.API.Models;

namespace MediTender.API.Services
{
    public interface IComparisonService
    {
        Task<List<OfferEvaluation>> CompareVendorsAsync(List<string> requirements, List<string> vendorNames);
    }
}