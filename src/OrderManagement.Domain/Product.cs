namespace OrderManagement.Domain;
public class Product
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string Sku { get; set; } = "";
    public string Description { get; set; } = "";
    public decimal Price { get; set; }
    public int StockQuantity { get; set; }
    public bool IsActive { get; set; } = true;
    // Persistence config makes SQL Server generate this concurrency token. It is not
    // a date/time; the API encodes its bytes as Base64 for an editor's next write.
    public byte[] RowVersion { get; set; } = Array.Empty<byte>();
}
