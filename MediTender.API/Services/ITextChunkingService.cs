namespace MediTender.API.Services
{
    public interface ITextChunkingService
    {
        List<string> ChunkText(string text);
    }
}