using Microsoft.AspNetCore.Mvc;
using MediTender.API.Services;

namespace MediTender.API.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class DocumentController : ControllerBase
    {
        private readonly IPdfParsingService _pdfParsingService;
        private readonly ITextChunkingService _textChunkingService;
        private readonly IVectorStorageService _vectorStorageService;

        public DocumentController(
            IPdfParsingService pdfParsingService, 
            ITextChunkingService textChunkingService, 
            IVectorStorageService vectorStorageService)
        {
            _pdfParsingService = pdfParsingService;
            _textChunkingService = textChunkingService;
            _vectorStorageService = vectorStorageService;
        }

        [HttpPost("upload-pdf")]
        public async Task<IActionResult> UploadPdfAsync(IFormFile file)
        {
            if (file == null || file.Length == 0)
            {
                return BadRequest("Please upload a valid PDF file.");
            }

            try
            {
                using var stream = file.OpenReadStream();
                var extractedText = _pdfParsingService.ExtractTextFromPdf(stream);
                
                var chunks = _textChunkingService.ChunkText(extractedText);

                await _vectorStorageService.SaveChunksToQdrantAsync(file.FileName, chunks);

                return Ok(new { 
                    Message = "upload and processing successful", 
                    ChunksCount = chunks.Count 
                });
            }
            catch (Exception ex)
            {
                return StatusCode(500, $"error in processing {ex.Message}");
            }
        }
    }
}