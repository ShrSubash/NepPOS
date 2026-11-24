using NepPOS.Domain.Entities;

namespace NepPOS.Presentation.Register.Components.Shared;

/// <summary>What the Return window hands the sell screen: the original sale and the items coming back.</summary>
public sealed record ReturnSelection(SaleTransaction Sale, IReadOnlyList<ReturnSelection.Item> Items)
{
    public sealed record Item(SaleLineItem Line, int Quantity, string Reason, bool Restock);
}
