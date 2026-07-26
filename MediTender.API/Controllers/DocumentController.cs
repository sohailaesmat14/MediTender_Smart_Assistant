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
        public async Task<IActionResult> UploadPdfAsync([FromForm] IFormFile file, [FromForm] string documentType, [FromForm] string vendorName = "")
        {
            if (file == null || file.Length == 0)
                return BadRequest("Invalid file.");

            if (string.IsNullOrWhiteSpace(documentType))
                return BadRequest("Document type (Standard or Offer) is required.");

            if (documentType == "Offer" && string.IsNullOrWhiteSpace(vendorName))
                return BadRequest("Vendor name is required for offers.");

            try
            {
                using var stream = file.OpenReadStream();
                var extractedText = _pdfParsingService.ExtractTextFromPdf(stream);
                var chunks = _textChunkingService.ChunkText(extractedText);
                
                await _vectorStorageService.SaveChunksToQdrantAsync(file.FileName, documentType, vendorName, chunks);

                return Ok(new { 
                    Message = "Success", 
                    DocumentType = documentType,
                    Vendor = vendorName,
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
        public class ComparisonRequest 
        { 
            public List<string> Requirements { get; set; } = new(); 
        }

        [HttpPost("compare")]
        public async Task<IActionResult> CompareOffer([FromBody] ComparisonRequest request, [FromServices] IComparisonService comparisonService)
        {
            if (request.Requirements == null || !request.Requirements.Any())
                return BadRequest();

            try
            {
                var results = await comparisonService.CompareOfferAsync(request.Requirements);
                return Ok(results);
            }
            catch (Exception ex)
            {
                return StatusCode(500, ex.Message);
            }
        }
        [HttpGet("extract-standard/{fileName}")]
        public async Task<IActionResult> ExtractStandardRequirements(string fileName, [FromServices] IStandardExtractionService extractionService)
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
    }

}