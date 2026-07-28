using MediTender.API.Models;

namespace MediTender.API.Services
{
    public interface IComparisonService
    {
        Task<List<OfferEvaluation>> CompareVendorsAsync(int tenderId, List<Standard> requirements, List<string> vendorNames);
    }
}