using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using MediTender.API.Services;
using MediTender.API.Data;
using MediTender.API.Models;
using Microsoft.AspNetCore.Authorization;

namespace MediTender.API.Controllers
{
    [Authorize]
    [ApiController]
    [Route("api/[controller]")]
    public class DocumentController : ControllerBase
    {
        private readonly IPdfParsingService _pdfParsingService;
        private readonly ITextChunkingService _textChunkingService;
        private readonly IVectorStorageService _vectorStorageService;
        private readonly ApplicationDbContext _dbContext;
        private readonly ILogger<DocumentController> _logger;

        public DocumentController(
            IPdfParsingService pdfParsingService, 
            ITextChunkingService textChunkingService, 
            IVectorStorageService vectorStorageService,
            ApplicationDbContext dbContext,
            ILogger<DocumentController> logger)
        {
            _pdfParsingService = pdfParsingService;
            _textChunkingService = textChunkingService;
            _vectorStorageService = vectorStorageService;
            _dbContext = dbContext;
            _logger = logger;
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
                await _vectorStorageService.DeleteExistingDocumentAsync(request.TenderId, request.DocumentType, request.VendorName);

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
                _logger.LogError(ex, "Error uploading PDF for DocumentType: {DocumentType}, Vendor: {VendorName}", request.DocumentType, request.VendorName);
    
    return StatusCode(500, new { Message = "An internal server error occurred while processing your request. Please try again later." });
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
                _logger.LogError(ex, "Error processing Q&A request for Tender: {TenderId}, Vendor: {VendorName}", request.TenderId, request.VendorName);
                return StatusCode(500, new { Message = "An internal server error occurred while answering the question. Please try again." });
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
        public async Task<IActionResult> CompareVendors([FromBody] MultiComparisonRequest request, [FromServices] IComparisonService comparisonService, CancellationToken cancellationToken)
        {
            if (request.VendorNames == null || !request.VendorNames.Any())
                return BadRequest("Vendor names list cannot be empty.");
            
            int cost = request.VendorNames.Count * 15;
            if (!TryConsumeQuota(cost, out int remaining))
            {
                return BadRequest(new { Message = $"❌ Your current balance ({remaining} points) isn't enough. You need ({cost} points)." });
            }

            try
            {
                var dbRequirements = await _dbContext.Standards
                    .Where(s => s.TenderId == request.TenderId)
                    .ToListAsync(cancellationToken);

                if (!dbRequirements.Any())
                    return BadRequest("No standard requirements found for this tender. Please run the extraction phase first.");

                var results = await comparisonService.CompareVendorsAsync(request.TenderId, dbRequirements, request.VendorNames, cancellationToken);
                return Ok(results);
            }
            catch (OperationCanceledException)
            {
                RefundQuota(cost); 
                return StatusCode(499, "Client closed the request.");
            }
            catch (Exception ex)
            {
                RefundQuota(cost); 
                _logger.LogError(ex, "Error during multi-vendor comparison for Tender: {TenderId}", request.TenderId);
                return StatusCode(500, new { Message = "An internal server error occurred during vendor comparison. Please review the logs." });
            }
        }
        public class MultiComparisonRequest 
        { 
            public int TenderId { get; set; } = 1; 
           
            public List<string> VendorNames { get; set; } = new();
        }

        [HttpGet("extract-standard")]
        public async Task<IActionResult> ExtractStandardRequirements([FromQuery] string fileName, [FromQuery] int tenderId, [FromServices] IStandardExtractionService extractionService)
        {
            if (string.IsNullOrWhiteSpace(fileName))
                return BadRequest("File name is required.");

            try
            {
                var tenderExists = await _dbContext.Tenders.AnyAsync(t => t.Id == tenderId);
                if (!tenderExists)
                {
                    _dbContext.Tenders.Add(new Tender { Id = tenderId, Title = "System Generated Tender", Description = "Auto-generated" });
                    await _dbContext.SaveChangesAsync();
                }

                var requirements = await extractionService.ExtractRequirementsAsync(fileName, tenderId);
                return Ok(new { Requirements = requirements });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error extracting standard requirements for Tender: {TenderId}, File: {FileName}", tenderId, fileName);
                return StatusCode(500, new { Message = "An internal server error occurred while extracting requirements." });
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

                if (_dbContext.Database.IsSqlServer())
                {
                    await _dbContext.Database.ExecuteSqlRawAsync("DBCC CHECKIDENT ('Tenders', RESEED, 0)");
                    await _dbContext.Database.ExecuteSqlRawAsync("DBCC CHECKIDENT ('VendorOffers', RESEED, 0)");
                    await _dbContext.Database.ExecuteSqlRawAsync("DBCC CHECKIDENT ('OfferEvaluations', RESEED, 0)");
                }
                else if (_dbContext.Database.ProviderName == "Npgsql.EntityFrameworkCore.PostgreSQL") 
                {
                    await _dbContext.Database.ExecuteSqlRawAsync("TRUNCATE TABLE \"Tenders\", \"VendorOffers\", \"OfferEvaluations\" RESTART IDENTITY CASCADE");
                }
                else if (_dbContext.Database.ProviderName == "Microsoft.EntityFrameworkCore.Sqlite") 
                {
                    await _dbContext.Database.ExecuteSqlRawAsync("DELETE FROM sqlite_sequence WHERE name IN ('Tenders', 'VendorOffers', 'OfferEvaluations')");
                }

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
                _logger.LogError(ex, "Error during system reset operation.");
                return StatusCode(500, new { Message = "An internal server error occurred while resetting the system." });
            }
        }
        public class FileUploadRequest
        {
            public IFormFile? File { get; set; }
            public string DocumentType { get; set; } = string.Empty;
            public string VendorName { get; set; } = string.Empty;
            public int TenderId { get; set; } = 1;
        }
        

        private static readonly object _quotaLock = new object();
        private static readonly Dictionary<string, int> _userQuotas = new();
        private static DateTime _lastResetDate = DateTime.UtcNow.Date;

        private string GetCurrentUsername()
        {
            return User.Claims.FirstOrDefault(c => c.Type == System.Security.Claims.ClaimTypes.NameIdentifier || c.Type == "sub")?.Value ?? "committee";
        }

        private bool TryConsumeQuota(int cost, out int remaining)
        {
            lock (_quotaLock)
            {
                if (DateTime.UtcNow.Date > _lastResetDate)
                {
                    _userQuotas.Clear();
                    _lastResetDate = DateTime.UtcNow.Date;
                }

                string username = GetCurrentUsername();
                if (!_userQuotas.ContainsKey(username))
                    _userQuotas[username] = 200; 

                if (_userQuotas[username] >= cost)
                {
                    _userQuotas[username] -= cost;
                    remaining = _userQuotas[username];
                    return true;
                }

                remaining = _userQuotas[username];
                return false;
            }
        }

        private void RefundQuota(int amount)
        {
            lock (_quotaLock)
            {
                string username = GetCurrentUsername();
                if (_userQuotas.ContainsKey(username))
                {
                    _userQuotas[username] += amount;
                }
            }
        }

        [HttpPost("check-quota")]
        public IActionResult CheckQuota([FromBody] QuotaRequest request)
        {
            int cost = request.VendorCount * 15;
            lock (_quotaLock)
            {
                string username = GetCurrentUsername();
                if (!_userQuotas.ContainsKey(username)) _userQuotas[username] = 200;
                
                if (_userQuotas[username] >= cost)
                    return Ok(new { Success = true, RemainingQuota = _userQuotas[username] });
                    
                return BadRequest(new { Success = false, Message = $"❌ Your current balance ({_userQuotas[username]} points) isn't enough. You need ({cost} points)." });
            }
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
                _logger.LogError(ex, "Error overriding evaluation for Tender: {TenderId}, Vendor: {VendorName}", request.TenderId, request.VendorName);
                return StatusCode(500, new { Message = "An internal server error occurred while processing the evaluation override." });
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
                _logger.LogError(ex, "Error overriding vendor decision for Tender: {TenderId}, Vendor: {VendorName}", request.TenderId, request.VendorName);
                return StatusCode(500, new { Message = "An internal server error occurred while processing the vendor decision override." });
            }
        }

        public class VendorOverrideRequest
        {
            public int TenderId { get; set; }
            public string VendorName { get; set; } = string.Empty;
        }
    }
}