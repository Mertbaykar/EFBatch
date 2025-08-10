
namespace EFBatch.API
{
    public class Category
    {
        public int Id { get; init; }
        public string Name { get; init; }
        public ICollection<Product> Products { get; init; } = [];
    }
}
