// Adapted from EdgeTTS.DotNet v0.4.0 (MIT). See NOTICE.md.
namespace SeleVox.App.EdgeTransport;

using System.Security.Cryptography;
using System.Text;
using System.Globalization;

/// <summary>
/// Pinned upstream token formatting; no clock correction or identity rotation.
/// </summary>
internal static class Drm
{
    private const long WinEpoch = 11644473600;
    /// <summary>
    /// Generates the Sec-MS-GEC token value.
    /// </summary>
    public static string GenerateSecMsGec() => GenerateSecMsGec(DateTimeOffset.UtcNow.ToUnixTimeSeconds());

    internal static string GenerateSecMsGec(long unixSeconds)
    {
        var ticks = unixSeconds;

        // Switch to Windows file time epoch (1601-01-01 00:00:00 UTC)
        ticks += WinEpoch;

        // Round down to the nearest 5 minutes (300 seconds)
        ticks -= ticks % 300;

        // Convert the ticks to 100-nanosecond intervals (Windows file time format)
        ticks *= 10_000_000; // 1 second = 10,000,000 100-nanosecond intervals

        // Create the string to hash by concatenating the ticks and the trusted client token
        var strToHash = ticks.ToString("F0", CultureInfo.InvariantCulture) + Constants.TrustedClientToken;

        // Compute the SHA256 hash and return the uppercased hex digest
        var hash = SHA256.HashData(Encoding.ASCII.GetBytes(strToHash));

        return BitConverter.ToString(hash).Replace("-", "").ToUpper();
    }

}
