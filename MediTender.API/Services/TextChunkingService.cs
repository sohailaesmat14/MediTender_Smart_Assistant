#pragma warning disable SKEXP0050

using Microsoft.SemanticKernel.Text;

namespace MediTender.API.Services
{
    public class TextChunkingService : ITextChunkingService
    {
        public List<string> ChunkText(string text)
        {
            var lines = TextChunker.SplitPlainTextLines(text, 100);
            var paragraphs = TextChunker.SplitPlainTextParagraphs(lines, 800);
            
            return paragraphs;
        }
    }
}