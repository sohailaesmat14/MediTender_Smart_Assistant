using MediTender.API.Models;

namespace MediTender.API.Services
{
    public interface IFinancialEvaluationService
    {
        Task<VendorOffer> EvaluateFinancialOfferAsync(int tenderId, string vendorName, bool isTechnicallyAccepted, decimal technicalScore);
    }
}