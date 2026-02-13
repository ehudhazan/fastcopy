using System.Globalization;
using System.Text.RegularExpressions;

namespace FastCopy.Core;

/// <summary>
/// Utility for parsing human-readable byte size strings (e.g., "100MB", "1.5GB").
/// </summary>
public static partial class ByteSizeParser
{
    private const long KB = 1024;
    private const long MB = 1024 * KB;
    private const long GB = 1024 * MB;
    private const long TB = 1024 * GB;

    /// <summary>
    /// Parse a human-readable byte size string into bytes.
    /// Supports formats: "100", "100B", "100KB", "100K", "100MB", "100M", "100GB", "100G", "100TB", "100T".
    /// Supports decimal values: "1.5GB", "0.5MB".
    /// </summary>
    /// <param name="input">The human-readable size string (case-insensitive).</param>
    /// <returns>Size in bytes.</returns>
    /// <exception cref="ArgumentException">Thrown when the input format is invalid.</exception>
    public static long Parse(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            throw new ArgumentException("Input cannot be null or empty.", nameof(input));
        }

        input = input.Trim().ToUpperInvariant();

        // Try to match pattern: <number><unit>
        // Unit is optional (defaults to bytes)
        var match = ParseRegex().Match(input);

        if (!match.Success)
        {
            throw new ArgumentException($"Invalid byte size format: '{input}'. Expected format: <number>[unit] (e.g., 100MB, 1.5GB).", nameof(input));
        }

        string numberPart = match.Groups[1].Value;
        string unitPart = match.Groups[2].Value;

        if (!double.TryParse(numberPart, NumberStyles.Float, CultureInfo.InvariantCulture, out double number))
        {
            throw new ArgumentException($"Invalid number: '{numberPart}'.", nameof(input));
        }

        if (number < 0)
        {
            throw new ArgumentException("Byte size cannot be negative.", nameof(input));
        }

        long multiplier = unitPart switch
        {
            "" or "B" => 1,
            "K" or "KB" => KB,
            "M" or "MB" => MB,
            "G" or "GB" => GB,
            "T" or "TB" => TB,
            _ => throw new ArgumentException($"Unknown unit: '{unitPart}'. Supported units: B, K, KB, M, MB, G, GB, T, TB.", nameof(input))
        };

        // Check for overflow
        double result = number * multiplier;
        if (result > long.MaxValue)
        {
            throw new ArgumentException($"Resulting byte size exceeds maximum value: {result}.", nameof(input));
        }

        return (long)result;
    }

    /// <summary>
    /// Try to parse a human-readable byte size string.
    /// </summary>
    /// <param name="input">The input string.</param>
    /// <param name="bytes">The parsed byte value (output).</param>
    /// <returns>True if parsing succeeded, false otherwise.</returns>
    public static bool TryParse(string input, out long bytes)
    {
        try
        {
            bytes = Parse(input);
            return true;
        }
        catch
        {
            bytes = 0;
            return false;
        }
    }

    // Regex pattern: optional whitespace, number (int or decimal), optional unit
    [GeneratedRegex(@"^\s*(\d+(?:\.\d+)?)\s*([KMGTB]{0,2})\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex ParseRegex();
}
