using System.ComponentModel.DataAnnotations;
using System.Data;
using OrderManagement.Domain;
namespace OrderManagement.Application;

public class BusinessException : Exception
{
    public int Status { get; }
    public BusinessException(string message, int status = 400) : base(message) => Status = status;
}
public record ProductWrite(
    [Required, StringLength(160, MinimumLength = 2)] string Name,
    [Required, StringLength(40, MinimumLength = 2), RegularExpression("^[A-Z0-9-]+$")] string Sku,
    [Required, StringLength(2000)] string Description,
    [Range(typeof(decimal), "0.01", "999999999.99")] decimal Price,
    [Range(0, 1000000)] int StockQuantity,
    string? RowVersion);
public record ItemInput([Range(1, int.MaxValue)] int ProductId, [Range(1, 10000)] int Quantity);
public record CreateOrderRequest([Required, MinLength(1), MaxLength(100)] List<ItemInput> Items);
public record EditItemRequest([Range(1, 10000)] int Quantity, [Required] string RowVersion);
public record AddItemRequest([Range(1, int.MaxValue)] int ProductId, [Range(1, 10000)] int Quantity, [Required] string RowVersion);
public record VersionRequest([Required] string RowVersion);
public record StatusRequest(OrderStatus Status, [Required] string RowVersion);
public record OrderItemDto(int Id, int ProductId, string ProductName, int Quantity, decimal UnitPrice);
public record OrderDto(int Id, string CustomerId, DateTime CreatedAtUtc, OrderStatus Status, decimal TotalAmount, string RowVersion, List<OrderItemDto> Items);
public class OrderQuery
{
    [Range(1, 1000000)] public int PageNumber { get; set; } = 1;
    [Range(1, 100)] public int PageSize { get; set; } = 12;
    public OrderStatus? Status { get; set; }
    [StringLength(100)] public string? Search { get; set; }
    [RegularExpression("^(newest|oldest|total)$")] public string Sort { get; set; } = "newest";
}
public interface IOrderRepository
{
    Task<Page<OrderDto>> ListAsync(string userId, bool all, OrderQuery query, CancellationToken ct);
    Task<OrderDto?> ReadAsync(int id, CancellationToken ct);
    Task<Order?> FindAsync(int id, CancellationToken ct);
    void Add(Order order);
    void RemoveItem(OrderItem item);
}
public interface IWorkTransaction : IAsyncDisposable { Task CommitAsync(CancellationToken ct); }
public interface IEmailSender { Task SendAsync(string email, string subject, string link, CancellationToken ct); }
