using System.Text;
using UglyToad.PdfPig;

namespace MediTender.API.Services
{
    public class PdfParsingService : IPdfParsingService
    {
        public string ExtractTextFromPdf(Stream pdfStream)
        {
            StringBuilder text = new StringBuilder();
            
            using (PdfDocument document = PdfDocument.Open(pdfStream))
            {
                foreach (var page in document.GetPages())
                {
                    text.Append(page.Text);
                    text.Append(" "); 
                }
            }
            
            return text.ToString();
        }
    }
}