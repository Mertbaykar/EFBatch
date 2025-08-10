

namespace EFBatch.API
{
    public class ProductRepository : IProductRepository
    {
        private readonly ECommerceContext _context;
        public ProductRepository(ECommerceContext context)
        {
            _context = context;
        }

        public async Task UpdateProducts(int categoryId)
        {
            //using var transaction = await _context.Database.BeginTransactionAsync();

            try
            {
                //var products = _context.Product.ToList();
                //products.ForEach(p => p.IsActive = false);

                _context.BatchUpdate(ctx => ctx.Product,
                    setPropertyCalls => setPropertyCalls
                                        .SetProperty(p => p.IsActive, p => false)
                                        );

                _context.BatchUpdate(ctx => ctx.Product.Where(x => x.CategoryId == categoryId),
                    setPropertyCalls => setPropertyCalls
                                        .SetProperty(p => p.IsActive, p => true)
                                        );
                //return Task.CompletedTask;
                //_context.Product.Where(x => x.CategoryId == categoryId)
                //    .ExecuteUpdate(setPropertyCalls => setPropertyCalls
                //                        .SetProperty(p => p.IsActive, p => false));


                await _context.SaveChangesAsync();
                //throw new Exception("Simulated exception for testing rollback"); // Simulate an error to test rollback
                //await transaction.CommitAsync();
            }
            catch (Exception ex)
            {
                //await transaction.RollbackAsync();
                // Handle exception (logging, rethrowing, etc.)
                throw;
            }
        }
    }

    public interface IProductRepository
    {
        Task UpdateProducts(int categoryId);
    }
}




