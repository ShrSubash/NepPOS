namespace NepPOS.Domain.Entities;

public sealed class Product
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public decimal Price { get; set; }
    public decimal? CostPrice { get; set; }
    public string Category { get; set; } = string.Empty;
    public string? ImageUrl { get; set; }
    public string? Barcode { get; set; }
    public string? Plu { get; set; }
    public decimal StockOnHand { get; set; }
    public string? ProductUrl { get; set; }
    public int PointsPerUnit { get; set; }
    public bool EnableOnWebsite { get; set; }
    public bool IsActive { get; set; } = true;
}