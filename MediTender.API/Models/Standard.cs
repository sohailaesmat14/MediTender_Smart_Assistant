namespace MediTender.API.Models
{
    public class Standard
    {
        public int Id { get; set; }
        
        public int TenderId { get; set; } 
        
        public string ItemName { get; set; } = string.Empty; 
        public string Description { get; set; } = string.Empty;
        public bool IsMandatory { get; set; } = true; 
        
        public Tender? Tender { get; set; }
    }
}