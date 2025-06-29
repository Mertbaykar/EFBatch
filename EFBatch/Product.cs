

using System.ComponentModel.DataAnnotations.Schema;

namespace EFBatch
{
    public class Product
    {
        public int Id { get; init; }
        public string Name { get; init; }
        public decimal Price { get; init; }
        public bool IsActive { get; set; }
        public int CategoryId { get; init; }
        [ForeignKey(nameof(CategoryId))]
        public Category Category { get; init; }
    }
}
