namespace MediTender.API.Services
{
    public interface IPdfParsingService
    {
        string ExtractTextFromPdf(Stream pdfStream);
    }
}