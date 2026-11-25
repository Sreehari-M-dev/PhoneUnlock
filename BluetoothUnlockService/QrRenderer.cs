using System.Collections;
using System.Text;
using QRCoder;

namespace BluetoothUnlockService;

/// <summary>
/// Renders a QR code into the terminal using Unicode half blocks.
///
/// Two details make the difference between "scannable" and "garbage":
///
///  1. ANSI colours. On a dark terminal the default background is dark, which
///     produces a light-on-dark (inverted) code. Phones often refuse those, so
///     the renderer paints an explicit white background with black modules.
///  2. UTF-8 output. Windows consoles default to an OEM code page, which turns
///     the block characters into mojibake (the same reason the old banner
///     printed "?????" instead of box drawing).
/// </summary>
public static class QrRenderer
{
    // QRCoder's ModuleMatrix already carries the 4-module light border (quiet
    // zone) that ISO/IEC 18004 requires, so the matrix is drawn as-is. Adding
    // more padding would only make the code wider than it must be.

    private const string WhiteBackground = "\u001b[48;2;255;255;255m";
    private const string BlackForeground = "\u001b[38;2;0;0;0m";
    private const string Reset = "\u001b[0m";

    /// <summary>Prints the code, plus a warning when colours are unavailable.</summary>
    public static void Print(string payload, TextWriter? output = null)
    {
        output ??= Console.Out;

        bool ansi = SupportsAnsi();
        foreach (string line in RenderLines(payload, ansi))
        {
            output.WriteLine(line);
        }

        if (!ansi)
        {
            output.WriteLine();
            output.WriteLine(
                "  NOTE: this console does not support colours, so the code is");
            output.WriteLine(
                "  light-on-dark. If your phone will not scan it, use Windows");
            output.WriteLine("  Terminal (the default on Windows 11).");
        }
    }

    /// <summary>
    /// Builds the printable lines. <paramref name="ansi"/> false yields plain
    /// glyphs (best effort, no background colour).
    /// </summary>
    public static IReadOnlyList<string> RenderLines(string payload, bool ansi)
    {
        using var generator = new QRCodeGenerator();
        using QRCodeData data = generator.CreateQrCode(payload, QRCodeGenerator.ECCLevel.M);

        List<BitArray> matrix = data.ModuleMatrix;
        int size = matrix.Count;

        var lines = new List<string>();

        // Each character cell covers two module rows (upper/lower half block).
        for (int y = 0; y < size; y += 2)
        {
            var line = new StringBuilder(size + 16);

            if (ansi)
            {
                line.Append(WhiteBackground).Append(BlackForeground);
            }

            for (int x = 0; x < size; x++)
            {
                bool top = IsDark(matrix, x, y, size);
                bool bottom = IsDark(matrix, x, y + 1, size);

                line.Append(top && bottom ? '█'
                    : top ? '▀'
                    : bottom ? '▄'
                    : ' ');
            }

            if (ansi)
            {
                line.Append(Reset);
            }

            lines.Add(line.ToString());
        }

        return lines;
    }

    private static bool IsDark(List<BitArray> matrix, int x, int y, int size)
    {
        if (x < 0 || y < 0 || x >= size || y >= size)
        {
            return false; // only y+1 on the last line, when size is odd
        }

        return matrix[y][x];
    }

    private static bool SupportsAnsi()
    {
        if (Console.IsOutputRedirected)
        {
            return false;
        }

        // Windows Terminal, VS Code and ConEmu all set TERM. The legacy
        // conhost does not, and would print the escape sequences literally.
        string? term = Environment.GetEnvironmentVariable("TERM");
        if (!string.IsNullOrEmpty(term) &&
            !term.Equals("dumb", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return Environment.GetEnvironmentVariable("WT_SESSION") != null ||
               Environment.GetEnvironmentVariable("TERM_PROGRAM") != null;
    }

    /// <summary>
    /// Switches the console to UTF-8 so block characters survive. Safe to call
    /// from both console and service contexts.
    /// </summary>
    public static void PrepareConsole()
    {
        try
        {
            if (!Console.IsOutputRedirected)
            {
                Console.OutputEncoding = Encoding.UTF8;
            }
        }
        catch
        {
            // Not a console, or the encoding cannot be changed.
        }
    }
}
