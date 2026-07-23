using Microsoft.AspNetCore.Mvc;
using MediTender.API.Services;

namespace MediTender.API.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class DocumentController : ControllerBase
    {
        private readonly IPdfParsingService _pdfParsingService;

        public DocumentController(IPdfParsingService pdfParsingService)
        {
            _pdfParsingService = pdfParsingService;
        }

        [HttpPost("upload-pdf")]
        public IActionResult UploadPdf(IFormFile file)
        {
            if (file == null || file.Length == 0)
            {
                return BadRequest("Please upload a valid PDF file.");
            }

            using (var stream = file.OpenReadStream())
            {
                var extractedText = _pdfParsingService.ExtractTextFromPdf(stream);
                
                return Ok(new { Text = extractedText });
            }
        }
    }
}