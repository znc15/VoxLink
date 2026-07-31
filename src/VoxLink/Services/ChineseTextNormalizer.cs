using System.ComponentModel;
using System.Runtime.InteropServices;
using VoxLink.Models;

namespace VoxLink.Services;

internal static class ChineseTextNormalizer
{
    private const uint SimplifiedChinese = 0x02000000;

    public static string Normalize(string text, LanguageOption language)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(language);
        return IsSimplifiedChinese(language) ? ToSimplified(text) : text;
    }

    internal static string ToSimplified(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return text;
        }

        var requiredLength = LCMapStringEx(
            "zh-CN",
            SimplifiedChinese,
            text,
            text.Length,
            null,
            0,
            IntPtr.Zero,
            IntPtr.Zero,
            0);
        if (requiredLength == 0)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Unable to normalize Chinese text.");
        }

        var output = new char[requiredLength];
        var written = LCMapStringEx(
            "zh-CN",
            SimplifiedChinese,
            text,
            text.Length,
            output,
            output.Length,
            IntPtr.Zero,
            IntPtr.Zero,
            0);
        if (written == 0)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Unable to normalize Chinese text.");
        }

        return new string(output, 0, written);
    }

    private static bool IsSimplifiedChinese(LanguageOption language) =>
        language.Culture.Equals("zh-CN", StringComparison.OrdinalIgnoreCase);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int LCMapStringEx(
        string localeName,
        uint mapFlags,
        string source,
        int sourceLength,
        [Out] char[]? destination,
        int destinationLength,
        IntPtr versionInformation,
        IntPtr reserved,
        nint sortHandle);
}
