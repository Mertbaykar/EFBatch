

using Microsoft.EntityFrameworkCore;

namespace EFBatch
{
    public class ECommerceContext : BatchingDbContext<ECommerceContext>
    {
        public ECommerceContext(DbContextOptions options) : base(options)
        {
        }

        public DbSet<Product> Product { get; init; }
        public DbSet<Category> Category { get; init; }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);
            modelBuilder.Entity<Product>()
                .HasOne(p => p.Category)
                .WithMany(c => c.Products)
                .HasForeignKey(p => p.CategoryId);

            modelBuilder.Entity<Category>()
                .HasMany(c => c.Products)
                .WithOne(p => p.Category)
                .HasForeignKey(p => p.CategoryId);

            modelBuilder.Entity<Product>()
                .Property(p => p.IsActive)
                .HasDefaultValue(true);
        }
    }
}
