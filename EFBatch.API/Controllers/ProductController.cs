using Microsoft.AspNetCore.Mvc;
using System.Diagnostics;

namespace EFBatch.API.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class ProductController : ControllerBase
    {

        private readonly ILogger<ProductController> _logger;
        private readonly IProductRepository _productRepository;

        public ProductController(ILogger<ProductController> logger, IProductRepository productRepository)
        {
            _logger = logger;
            _productRepository = productRepository;
        }

        [HttpPut("{categoryId}")]
        public async Task<IActionResult> Update([FromRoute] int categoryId)
        {
            try
            {
                var stopwatch = new Stopwatch();
                stopwatch.Start();
                await _productRepository.UpdateProducts(categoryId);
                stopwatch.Stop();

                return Ok(new { Milliseconds = stopwatch.ElapsedMilliseconds });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex.Message);
                return StatusCode(500, ex.Message);
            }
        }
    }
}
