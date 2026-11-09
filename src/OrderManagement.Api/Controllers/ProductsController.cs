using Microsoft.AspNetCore.Mvc;
using OrderManagement.Application;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Caching.Memory;
namespace OrderManagement.Api.Controllers;
// Concept: [ApiController] binds DTOs and returns validation ProblemDetails (400)
// before the action runs. Browsing is public; write actions require a backend policy.
[ApiController]
[Route("api/products")]
public class ProductsController : ControllerBase
{
    private readonly ProductService products;
    private readonly ProductAdminService admin;
    private readonly CatalogCache catalog;
    public ProductsController(ProductService products, ProductAdminService admin, CatalogCache catalog) => (this.products, this.admin, this.catalog) = (products, admin, catalog);
    [HttpGet]
    public async Task<Page<ProductDto>> List([FromQuery] ProductQuery query, CancellationToken ct)
    {
        // IncludeInactive is server-controlled here: a query-string flag must not expose
        // deactivated products. Only the authorized AdminList action enables it.
        query.IncludeInactive = false;
        // Production bypasses this per-process cache so replicas cannot serve different generations.
        if (!catalog.Enabled) return await products.ListAsync(query, ct);
        // Include every query dimension plus the cache generation. A write increments
        // that generation; an older in-flight read can only populate its old key.
        var key = $"{catalog.Version}:{query.PageNumber}:{query.PageSize}:{query.Sort}:{query.Search}";
        if (catalog.Cache.TryGetValue(key, out Page<ProductDto>? page)) return page!;
        page = await products.ListAsync(query, ct);
        // Size=1 counts entries (not bytes); CatalogCache caps the cache at 512 entries.
        catalog.Cache.Set(key, page, new MemoryCacheEntryOptions { AbsoluteExpirationRelativeToNow = TimeSpan.FromSeconds(30), Size = 1 });
        return page;
    }
    [HttpGet("admin"), Authorize(Policy = "ManageProducts")]
    public Task<Page<ProductDto>> AdminList([FromQuery] ProductQuery query, CancellationToken ct) { query.IncludeInactive = true; return products.ListAsync(query, ct); }
    [HttpPost, Authorize(Policy = "ManageProducts")]
    // Application service validates/saves business data first. Invalidate only after
    // success, then return 201 with a Location header pointing to the new resource.
    public async Task<IActionResult> Create(ProductWrite input, CancellationToken ct)
    {
        var id = await admin.SaveAsync(null, input, ct); catalog.Invalidate();
        return CreatedAtAction(nameof(Get), new { id }, await products.GetAsync(id, ct));
    }
    [HttpPut("{id:int}"), Authorize(Policy = "ManageProducts")]
    public async Task<IActionResult> Update(int id, ProductWrite input, CancellationToken ct)
    { await admin.SaveAsync(id, input, ct); catalog.Invalidate(); return NoContent(); }
    [HttpDelete("{id:int}"), Authorize(Policy = "ManageProducts")]
    public async Task<IActionResult> Deactivate(int id, [FromQuery] string rowVersion, CancellationToken ct)
    { await admin.DeactivateAsync(id, rowVersion, ct); catalog.Invalidate(); return NoContent(); }
    [HttpGet("{id:int:min(1)}")]
    public async Task<ActionResult<ProductDto>> Get(int id, CancellationToken ct)
    {
        var product = await products.GetAsync(id, ct);
        return product is null ? NotFound(new ProblemDetails { Status = 404, Title = "Product not found." }) : Ok(product);
    }
}
public class CatalogCache : IDisposable
{
    // Singleton lifetime is safe: this class holds cached DTOs, never a scoped DbContext.
    public bool Enabled { get; }
    public CatalogCache(IConfiguration configuration, IHostEnvironment environment)
        => Enabled = configuration.GetValue("CatalogCache:Enabled", environment.IsDevelopment());
    public MemoryCache Cache { get; } = new(new MemoryCacheOptions { SizeLimit = 512 });
    private long version;
    // Interlocked makes concurrent reads/increments atomic; it is not a distributed cache.
    public long Version => Interlocked.Read(ref version);
    public void Invalidate() => Interlocked.Increment(ref version);
    public void Dispose() => Cache.Dispose();
}
