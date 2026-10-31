using System.Text;
using NepPOS.Application.Hardware;

namespace NepPOS.Infrastructure.Adapters.Hardware;

/// <summary>
/// Turns a <see cref="ReceiptDocument"/> into ESC/POS bytes — the command language
/// understood by Epson TM, Star (ESC/POS mode), Bixolon and generic thermal printers.
/// </summary>
public static class EscPosRenderer
{
    private const byte Esc = 0x1B;
    private const byte Gs = 0x1D;
    private const byte Lf = 0x0A;

    /// <summary>
    /// ESC p 0 25 250: pulse pin 2 (drawer port 1) for 50 ms on / 500 ms off.
    /// The standard kick for a cash drawer plugged into the printer's RJ11 port.
    /// </summary>
    public static readonly byte[] DrawerKick = { Esc, (byte)'p', 0x00, 0x19, 0xFA };

    public static byte[] Render(ReceiptDocument receipt)
    {
        ArgumentNullException.ThrowIfNull(receipt);

        var bytes = new List<byte>(4096);
        bytes.AddRange(new byte[] { Esc, (byte)'@' }); // initialise: clears any previous mode

        foreach (var line in receipt.Lines)
        {
            bytes.AddRange(new byte[] { Esc, (byte)'a', line.Align switch
            {
                ReceiptAlign.Center => (byte)1,
                ReceiptAlign.Right => (byte)2,
                _ => (byte)0
            } });
            bytes.AddRange(new byte[] { Esc, (byte)'E', line.Bold ? (byte)1 : (byte)0 });
            bytes.AddRange(new byte[] { Gs, (byte)'!', line.Large ? (byte)0x01 : (byte)0x00 }); // double HEIGHT only

            bytes.AddRange(ToPrinterText(line.Text));
            bytes.Add(Lf);
        }

        // Reset styles, feed past the cutter, partial cut.
        bytes.AddRange(new byte[] { Esc, (byte)'E', 0, Gs, (byte)'!', 0, Esc, (byte)'a', 0 });
        bytes.AddRange(new byte[] { Gs, (byte)'V', 66, 3 });

        return bytes.ToArray();
    }

    /// <summary>Thermal printers default to a basic code page: keep plain ASCII, replace anything else.</summary>
    private static byte[] ToPrinterText(string text)
    {
        var sb = new StringBuilder(text.Length);
        foreach (var c in text)
            sb.Append(c is >= ' ' and <= '~' ? c : '?');
        return Encoding.ASCII.GetBytes(sb.ToString());
    }
}
