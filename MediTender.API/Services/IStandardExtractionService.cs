using System.Collections.Generic;
using System.Threading.Tasks;
using MediTender.API.Models;

namespace MediTender.API.Services
{
    public interface IStandardExtractionService
    {
        Task<List<Standard>> ExtractRequirementsAsync(string fileName);
    }
}