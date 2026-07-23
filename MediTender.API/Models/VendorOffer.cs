using System.ComponentModel.DataAnnotations.Schema;

namespace MediTender.API.Models
{
    public class VendorOffer
    {
        public int Id { get; set; }
        public int TenderId { get; set; }
        
        public string CompanyName { get; set; } = string.Empty; 
        
        [Column(TypeName = "decimal(18,2)")]
        public decimal TotalPrice { get; set; } 
        
        public bool IsAccepted { get; set; } = false; 
        
        [Column(TypeName = "decimal(18,2)")]
        public decimal EvaluationScore { get; set; } 
        
        public string AiRejectionReason { get; set; } = string.Empty; 
        
        public Tender? Tender { get; set; }
    }
}