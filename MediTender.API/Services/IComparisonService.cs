using MediTender.API.Models;

namespace MediTender.API.Services
{
    public interface IComparisonService
    {
        public Task<List<OfferEvaluation>> CompareVendorsAsync(int tenderId, List<Standard> requirements, List<string> vendorNames, CancellationToken cancellationToken = default);
    }
}