using System.Runtime.InteropServices;
using System.Windows;

namespace DcxMail.Services;

/// <summary>
/// The Windows clipboard is shared: while another program has it open (a clipboard manager, Remote Desktop, a password
/// manager, Office…) OpenClipboard fails with CLIPBRD_E_CANT_OPEN. WPF's Clipboard calls throw on the first failure, so
/// every copy in the app goes through here instead: it retries for about a second and never throws — a failed copy is
/// reported to the caller, never turned into an error that stops what the user was doing.
/// </summary>
public static class ClipboardHelper
{
    private const int Attempts = 10;

    /// <summary>The real copy (copy: true keeps the text after the app exits). The self-test swaps it for a fake so it
    /// never touches your clipboard.</summary>
    internal static Action<string> Setter = text => Clipboard.SetDataObject(text, true);

    /// <summary>Copies text; returns false if the clipboard stayed busy.</summary>
    public static bool TrySetText(string text)
    {
        for (int i = 0; i < Attempts; i++)
        {
            try
            {
                Setter(text);
                return true;
            }
            catch (Exception ex) when (ex is COMException or ExternalException)
            {
                Thread.Sleep(30 + 20 * i);
            }
        }
        return false;
    }

    /// <summary>Clears the clipboard only if it still holds <paramref name="text"/> (recovery codes after 60 s).</summary>
    public static void ClearIf(string text)
    {
        for (int i = 0; i < Attempts; i++)
        {
            try
            {
                if (Clipboard.ContainsText() && Clipboard.GetText() == text) Clipboard.Clear();
                return;
            }
            catch (Exception ex) when (ex is COMException or ExternalException) { Thread.Sleep(30 + 20 * i); }
        }
    }

    /// <summary>The status line for a copy attempt.</summary>
    public static string Say(bool copied, string what) =>
        copied ? $"{what} copied" : $"couldn't copy {what}: another program is using the clipboard — try again";
}
