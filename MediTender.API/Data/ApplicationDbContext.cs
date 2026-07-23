using MediTender.API.Models;
using Microsoft.EntityFrameworkCore;

namespace MediTender.API.Data
{
    public class ApplicationDbContext : DbContext
    {
        public ApplicationDbContext(DbContextOptions<ApplicationDbContext> options) : base(options)
        {
        }

        public DbSet<Tender> Tenders { get; set; }
        public DbSet<Standard> Standards { get; set; }
        public DbSet<VendorOffer> VendorOffers { get; set; }
    }
}