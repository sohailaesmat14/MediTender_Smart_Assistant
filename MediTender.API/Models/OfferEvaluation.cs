namespace MediTender.API.Models
{
    public class OfferEvaluation
    {
        public int Id { get; set; }
        public string VendorName { get; set; } = string.Empty;
        public DateTime EvaluationDate { get; set; } = DateTime.UtcNow;
        public int TotalScore { get; set; }
        public string FinalDecision { get; set; } = string.Empty;
        public List<EvaluationDetail> Details { get; set; } = new();
        public int TenderId { get; set; }
    }

    public class EvaluationDetail
    {
        public int Id { get; set; }
        public int OfferEvaluationId { get; set; }
        public string Requirement { get; set; } = string.Empty;
        public bool IsMandatory { get; set; }
        public string Status { get; set; } = string.Empty;
        public string Evidence { get; set; } = string.Empty;
        public int Score { get; set; }
    }
}