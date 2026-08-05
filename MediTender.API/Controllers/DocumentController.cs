using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using MediTender.API.Services;
using MediTender.API.Data;
using MediTender.API.Models;
using Microsoft.EntityFrameworkCore;

namespace MediTender.API.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class DocumentController : ControllerBase
    {
        private readonly IPdfParsingService _pdfParsingService;
        private readonly ITextChunkingService _textChunkingService;
        private readonly IVectorStorageService _vectorStorageService;
        private readonly ApplicationDbContext _dbContext;

        public DocumentController(
            IPdfParsingService pdfParsingService, 
            ITextChunkingService textChunkingService, 
            IVectorStorageService vectorStorageService,
            ApplicationDbContext dbContext)
        {
            _pdfParsingService = pdfParsingService;
            _textChunkingService = textChunkingService;
            _vectorStorageService = vectorStorageService;
            _dbContext = dbContext;
        }

        [HttpPost("upload-pdf")]
        public async Task<IActionResult> UploadPdfAsync([FromForm] FileUploadRequest request)
        {
            if (request.File == null || request.File.Length == 0)
                return BadRequest("Invalid file.");

            if (string.IsNullOrWhiteSpace(request.DocumentType))
                return BadRequest("Document type is required.");

            if (request.DocumentType.Contains("Offer") && string.IsNullOrWhiteSpace(request.VendorName))
                return BadRequest("Vendor name is required for offers.");

            try
            {
                using var stream = request.File.OpenReadStream();
                var extractedText = await Task.Run(() => _pdfParsingService.ExtractTextFromPdf(stream));
                var chunks = _textChunkingService.ChunkText(extractedText);
                
                await _vectorStorageService.SaveChunksToQdrantAsync(request.File.FileName, request.DocumentType, request.VendorName, chunks, request.TenderId);

                return Ok(new { 
                    Message = "Success", 
                    DocumentType = request.DocumentType,
                    Vendor = request.VendorName,
                    ChunksCount = chunks.Count 
                });
            }
            catch (Exception ex)
            {
                return StatusCode(500, ex.Message);
            }
        }
        
        [HttpPost("ask")]
        public async Task<IActionResult> AskQuestion([FromBody] QuestionRequest request, [FromServices] IRagService ragService)
        {
            if (string.IsNullOrWhiteSpace(request.Question))
                return BadRequest("Question is required.");

            try
            {
                var answer = await ragService.AnalyzeOfferAsync(request.Question, request.TenderId, request.VendorName);
                return Ok(new { Answer = answer });
            }
            catch (Exception ex)
            {
                return StatusCode(500, ex.Message);
            }
        }

        public class QuestionRequest 
        { 
            public string Question { get; set; } = string.Empty; 
            public int TenderId { get; set; }
            public string VendorName { get; set; } = string.Empty;
        }

        [HttpGet("history")]
        public async Task<IActionResult> GetHistory()
        {
            var history = await _dbContext.TenderInteractions
                .OrderByDescending(x => x.CreatedAt)
                .Take(10)
                .ToListAsync();
            
            return Ok(history);
        }


        [HttpPost("compare-vendors")]
        public async Task<IActionResult> CompareVendors([FromBody] MultiComparisonRequest request, [FromServices] IComparisonService comparisonService)
        {
            if (request.Requirements == null || !request.Requirements.Any())
                return BadRequest("Requirements list cannot be empty.");

            if (request.VendorNames == null || !request.VendorNames.Any())
                return BadRequest("Vendor names list cannot be empty.");

            try
            {
                // Check if the tender exists, if not, create a default one
                var tenderExists = await _dbContext.Tenders.AnyAsync(t => t.Id == request.TenderId);
                if (!tenderExists)
                {
                    var defaultTender = new Tender 
                    { 
                        Title = "System Generated Tender", 
                        Description = "Auto-generated for multi-vendor comparison." 
                    };
                    
                    _dbContext.Tenders.Add(defaultTender);
                    
                    // Turn on IDENTITY_INSERT if your DB requires specific IDs, 
                    // or let EF Core assign the ID and update your request.
                    await _dbContext.SaveChangesAsync();
                    
                    // Update the request with the newly generated Tender ID
                    request.TenderId = defaultTender.Id;
                }

                var results = await comparisonService.CompareVendorsAsync(request.TenderId, request.Requirements, request.VendorNames);
                return Ok(results);
            }
            catch (Exception ex)
            {
                return StatusCode(500, ex.Message);
            }
        }

        public class MultiComparisonRequest 
        { 
            public int TenderId { get; set; } = 1; 
            public List<Standard> Requirements { get; set; } = new(); 
            public List<string> VendorNames { get; set; } = new();
        }

        [HttpGet("extract-standard")]
        public async Task<IActionResult> ExtractStandardRequirements([FromQuery] string fileName, [FromServices] IStandardExtractionService extractionService)
        {
            if (string.IsNullOrWhiteSpace(fileName))
                return BadRequest("File name is required.");

            try
            {
                var requirements = await extractionService.ExtractRequirementsAsync(fileName);
                return Ok(new { Requirements = requirements });
            }
            catch (Exception ex)
            {
                return StatusCode(500, ex.Message);
            }
        }
        [HttpDelete("reset-system")]
        public async Task<IActionResult> ResetSystem([FromServices] Qdrant.Client.QdrantClient qdrantClient)
        {
            try
            {
                _dbContext.VendorOffers.RemoveRange(_dbContext.VendorOffers);
                _dbContext.EvaluationDetails.RemoveRange(_dbContext.EvaluationDetails);
                _dbContext.OfferEvaluations.RemoveRange(_dbContext.OfferEvaluations);
                _dbContext.Tenders.RemoveRange(_dbContext.Tenders);
                _dbContext.TenderInteractions.RemoveRange(_dbContext.TenderInteractions);
                await _dbContext.SaveChangesAsync();

                await _dbContext.Database.ExecuteSqlRawAsync("DBCC CHECKIDENT ('Tenders', RESEED, 0)");
                await _dbContext.Database.ExecuteSqlRawAsync("DBCC CHECKIDENT ('VendorOffers', RESEED, 0)");
                await _dbContext.Database.ExecuteSqlRawAsync("DBCC CHECKIDENT ('OfferEvaluations', RESEED, 0)");

                await qdrantClient.DeleteCollectionAsync("meditender_collection_v2");
                await qdrantClient.CreateCollectionAsync("meditender_collection_v2", 
                    new Qdrant.Client.Grpc.VectorParams { Size = 3072, Distance = Qdrant.Client.Grpc.Distance.Cosine });

                await qdrantClient.CreatePayloadIndexAsync("meditender_collection_v2", "fileName", Qdrant.Client.Grpc.PayloadSchemaType.Keyword);
                await qdrantClient.CreatePayloadIndexAsync("meditender_collection_v2", "documentType", Qdrant.Client.Grpc.PayloadSchemaType.Keyword);
                await qdrantClient.CreatePayloadIndexAsync("meditender_collection_v2", "vendorName", Qdrant.Client.Grpc.PayloadSchemaType.Keyword);
                await qdrantClient.CreatePayloadIndexAsync("meditender_collection_v2", "tenderId", Qdrant.Client.Grpc.PayloadSchemaType.Keyword);

                return Ok(new { Message = "System has been completely reset and is ready for a new demo!" });
            }
            catch (Exception ex)
            {
                return StatusCode(500, $"Reset failed: {ex.Message}");
            }
        }
        public class FileUploadRequest
        {
            public IFormFile? File { get; set; }
            public string DocumentType { get; set; } = string.Empty;
            public string VendorName { get; set; } = string.Empty;
            public int TenderId { get; set; } = 1;
        }
        private static int _dailyQuota = 200;
        private static DateTime _lastResetDate = DateTime.UtcNow.Date;

        [HttpPost("consume-quota")]
        public IActionResult ConsumeQuota([FromBody] QuotaRequest request)
        {
            if (DateTime.UtcNow.Date > _lastResetDate)
            {
                _dailyQuota = 200;
                _lastResetDate = DateTime.UtcNow.Date;
            }

            int expectedCost = request.VendorCount * 15;

            if (_dailyQuota >= expectedCost)
            {
                _dailyQuota -= expectedCost;
                return Ok(new { Success = true, RemainingQuota = _dailyQuota });
            }

            return BadRequest(new { 
                Success = false, 
                Message = $"❌ Your current balance ({_dailyQuota} points) isn't enough. You need ({expectedCost} points)." 
            });
        }

        public class QuotaRequest
        {
            public int VendorCount { get; set; }
        }
        [HttpPost("override-evaluation")]
        public async Task<IActionResult> OverrideEvaluation([FromBody] OverrideRequest request)
        {
            try
            {
                var evaluation = await _dbContext.OfferEvaluations
                    .Include(e => e.Details)
                    .FirstOrDefaultAsync(e => e.TenderId == request.TenderId && e.VendorName == request.VendorName);

                if (evaluation == null) 
                    return NotFound("Evaluation not found in database.");

                var detail = evaluation.Details.FirstOrDefault(d => d.Requirement == request.Requirement);
                if (detail == null) 
                    return NotFound("Requirement not found in this evaluation.");

                detail.Status = "Met";
                detail.Evidence = "✅ Manually verified by committee.";
                detail.Score = detail.IsMandatory ? 20 : 10;

                evaluation.TotalScore = evaluation.Details.Sum(d => d.Score);

                bool hasFailedMandatory = evaluation.Details.Any(d => d.IsMandatory && (d.Status == "Not Met" || d.Status == "Error"));
                bool hasPartialOrMissingMandatory = evaluation.Details.Any(d => d.IsMandatory && (d.Status == "Partially Met" || d.Status == "Not Mentioned"));

                if (hasFailedMandatory)
                    evaluation.FinalDecision = "Recommended for Rejection";
                else if (hasPartialOrMissingMandatory)
                    evaluation.FinalDecision = "Pending Manual Review";
                else
                    evaluation.FinalDecision = "Recommended for Acceptance";

                var vendorOffer = await _dbContext.VendorOffers.FirstOrDefaultAsync(v => v.TenderId == request.TenderId && v.CompanyName == request.VendorName);
                if (vendorOffer != null)
                {
                    vendorOffer.IsAccepted = evaluation.FinalDecision == "Recommended for Acceptance" || evaluation.FinalDecision == "Pending Manual Review";
                    vendorOffer.EvaluationScore = evaluation.TotalScore;
                }

                await _dbContext.SaveChangesAsync();

                return Ok(new { Message = "Override saved to database successfully" });
            }
            catch (Exception ex)
            {
                return StatusCode(500, ex.Message);
            }
        }

        public class OverrideRequest
        {
            public int TenderId { get; set; }
            public string VendorName { get; set; } = string.Empty;
            public string Requirement { get; set; } = string.Empty;
        }
        [HttpPost("override-vendor-decision")]
        public async Task<IActionResult> OverrideVendorDecision([FromBody] VendorOverrideRequest request)
        {
            try
            {
                var evaluation = await _dbContext.OfferEvaluations
                    .Include(e => e.Details)
                    .FirstOrDefaultAsync(e => e.TenderId == request.TenderId && e.VendorName == request.VendorName);

                if (evaluation == null) 
                    return NotFound("Evaluation not found in database.");

                // Loop through all pending mandatory items and approve them
                foreach (var detail in evaluation.Details.Where(d => d.IsMandatory && (d.Status == "Partially Met" || d.Status == "Not Mentioned")))
                {
                    detail.Status = "Met";
                    detail.Evidence = "✅ Vendor manually approved by committee.";
                    detail.Score = 20;
                }

                // Recalculate totals
                evaluation.TotalScore = evaluation.Details.Sum(d => d.Score);
                evaluation.FinalDecision = "Recommended for Acceptance";

                var vendorOffer = await _dbContext.VendorOffers.FirstOrDefaultAsync(v => v.TenderId == request.TenderId && v.CompanyName == request.VendorName);
                if (vendorOffer != null)
                {
                    vendorOffer.IsAccepted = true;
                    vendorOffer.EvaluationScore = evaluation.TotalScore;
                }

                await _dbContext.SaveChangesAsync();
                return Ok(new { Message = "Vendor completely approved and saved to database." });
            }
            catch (Exception ex)
            {
                return StatusCode(500, ex.Message);
            }
        }

        public class VendorOverrideRequest
        {
            public int TenderId { get; set; }
            public string VendorName { get; set; } = string.Empty;
        }
    }
}