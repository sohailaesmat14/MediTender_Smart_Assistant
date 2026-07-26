namespace MediTender.API.Models
{
    public class ComparisonResult
    {
        public string Requirement { get; set; } = string.Empty;
        public bool IsMandatory { get; set; }
        public string Status { get; set; } = string.Empty;
        public string Evidence { get; set; } = string.Empty;
        public int Score { get; set; }
    }
}