namespace NepPOS.Application.Abstractions;

/// <summary>
/// Identity of this physical register. Every sale is stamped with it, and invoice
/// numbers are sequenced per StoreId. Hard-coded in MauiProgram for now; it moves to
/// the store setup screen when multi-register configuration is built.
/// </summary>
public sealed record RegisterTerminal(string StoreId, string RegisterId);
