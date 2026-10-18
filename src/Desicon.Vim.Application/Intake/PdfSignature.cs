namespace Desicon.Vim.Application.Intake;

/// <summary>Trusts a file's content, not its name or declared type: a real PDF starts with "%PDF-".</summary>
public static class PdfSignature
{
    public static async Task<bool> IsPdfAsync(Func<CancellationToken, Task<Stream>> open, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(open);
        await using var s = await open(ct).ConfigureAwait(false);
        var header = new byte[5];
        var read = await s.ReadAtLeastAsync(header, header.Length, throwOnEndOfStream: false, ct).ConfigureAwait(false);
        return read == 5 && header is [(byte)'%', (byte)'P', (byte)'D', (byte)'F', (byte)'-'];
    }
}
