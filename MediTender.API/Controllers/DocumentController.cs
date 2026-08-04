using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using MediTender.API.Services;
using MediTender.API.Data;
using MediTender.API.Models;

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

            // تعديل بسيط هنا عشان يشتغل مع "TechnicalOffer" و "FinancialOffer"
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
                var answer = await ragService.AnalyzeOfferAsync(request.Question);
                return Ok(new { Answer = answer });
            }
            catch (Exception ex)
            {
                return StatusCode(500, ex.Message);
            }
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

        public class QuestionRequest 
        { 
        public string Question { get; set; } = string.Empty; 
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
                // 1. مسح وتنظيف جداول الـ SQL
                _dbContext.VendorOffers.RemoveRange(_dbContext.VendorOffers);
                _dbContext.EvaluationDetails.RemoveRange(_dbContext.EvaluationDetails);
                _dbContext.OfferEvaluations.RemoveRange(_dbContext.OfferEvaluations);
                _dbContext.Tenders.RemoveRange(_dbContext.Tenders);
                _dbContext.TenderInteractions.RemoveRange(_dbContext.TenderInteractions);
                await _dbContext.SaveChangesAsync();

                // السطور الجديدة: تصفير عدادات الـ Identity عشان نمنع مشكلة الـ Ghost ID
                await _dbContext.Database.ExecuteSqlRawAsync("DBCC CHECKIDENT ('Tenders', RESEED, 0)");
                await _dbContext.Database.ExecuteSqlRawAsync("DBCC CHECKIDENT ('VendorOffers', RESEED, 0)");
                await _dbContext.Database.ExecuteSqlRawAsync("DBCC CHECKIDENT ('OfferEvaluations', RESEED, 0)");

                // 2. مسح وتنظيف قاعدة بيانات Qdrant
                await qdrantClient.DeleteCollectionAsync("meditender_collection_v2");
                await qdrantClient.CreateCollectionAsync("meditender_collection_v2", 
                    new Qdrant.Client.Grpc.VectorParams { Size = 768, Distance = Qdrant.Client.Grpc.Distance.Cosine });

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
    }
}