using System.ComponentModel.DataAnnotations;
using OrderManagement.Domain;
namespace OrderManagement.Application;
// DTOs are API/application contracts, not tracked persistence entities. Base64 carries
// SQL Server's binary rowversion through JSON; it is a version, not a secret or date.
public record ProductDto(int Id, string Name, string Sku, string Description, decimal Price, int StockQuantity, bool IsActive, string RowVersion);
public record Page<T>(IReadOnlyList<T> Items, int TotalCount, int PageNumber, int PageSize);
public class ProductQuery
{
    [Range(1, 1000000)] public int PageNumber { get; set; } = 1;
    // Bound request cost; a client must not request an unlimited catalog in one response.
    [Range(1, 100)] public int PageSize { get; set; } = 12;
    [StringLength(100)] public string? Search { get; set; }
    // An allowlist maps names to known LINQ expressions; never inject client text into SQL.
    [RegularExpression("^(name|price|price_desc)$")] public string Sort { get; set; } = "name";
    public bool IncludeInactive { get; set; }
}
public class ProductAdminService
{
    private readonly IProductRepository products;
    private readonly IUnitOfWork work;
    public ProductAdminService(IProductRepository products, IUnitOfWork work) => (this.products, this.work) = (products, work);
    public async Task<int> SaveAsync(int? id, ProductWrite input, CancellationToken ct)
    {
        var product = id.HasValue ? await products.FindForUpdateAsync(id.Value, ct) ?? throw new BusinessException("Product not found.", 404) : new Product();
        // First reject an editor's stale version. EF also checks the loaded rowversion
        // during UPDATE, covering another writer that commits after this read.
        if (id.HasValue && Convert.ToBase64String(product.RowVersion) != input.RowVersion) throw new BusinessException("Product changed. Reload before saving.", 409);
        // Reject unsupported precision rather than silently letting SQL round a submitted price.
        if (decimal.Round(input.Price, 2) != input.Price) throw new BusinessException("Price allows at most two decimal places.");
        product.Name = input.Name.Trim(); product.Sku = input.Sku.Trim(); product.Description = input.Description.Trim();
        product.Price = input.Price; product.StockQuantity = input.StockQuantity;
        if (product.Name.Length < 2) throw new BusinessException("Product name is too short.");
        if (!id.HasValue) products.Add(product);
        await work.SaveChangesAsync(ct);
        return product.Id;
    }
    public async Task DeactivateAsync(int id, string version, CancellationToken ct)
    {
        var product = await products.FindForUpdateAsync(id, ct) ?? throw new BusinessException("Product not found.", 404);
        if (Convert.ToBase64String(product.RowVersion) != version) throw new BusinessException("Product changed. Reload before saving.", 409);
        // Deactivate instead of deleting: existing order items still reference this product.
        product.IsActive = false;
        await work.SaveChangesAsync(ct);
    }
}
public interface IProductRepository
{
    Task<Page<ProductDto>> ListAsync(ProductQuery query, CancellationToken ct);
    Task<ProductDto?> GetAsync(int id, CancellationToken ct);
    Task<Product?> FindForUpdateAsync(int id, CancellationToken ct);
    void Add(Product product);
}
public interface IUnitOfWork
{
    // The service owns when changes become durable. Repository Add/Find operations do not save.
    Task<int> SaveChangesAsync(CancellationToken ct);
    Task<IWorkTransaction> BeginAsync(System.Data.IsolationLevel isolation, CancellationToken ct);
}
public class ProductService
{
    private readonly IProductRepository products;
    public ProductService(IProductRepository products) => this.products = products;
    public Task<Page<ProductDto>> ListAsync(ProductQuery query, CancellationToken ct) => products.ListAsync(query, ct);
    public Task<ProductDto?> GetAsync(int id, CancellationToken ct) => products.GetAsync(id, ct);
}
