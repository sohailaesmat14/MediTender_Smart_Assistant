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
            
            string extractedText = text.ToString();

            extractedText = extractedText.Replace("✓", " Yes ")
                                         .Replace("✔", " Yes ")
                                         .Replace("☑", " Yes ")
                                         .Replace("O", " Optional ") 
                                         .Replace("?", " ");
            
            int maxSafeLength = 15000;
            if (extractedText.Length > maxSafeLength)
            {
                extractedText = extractedText.Substring(0, maxSafeLength);
            }

            return extractedText;
        }
    }
}