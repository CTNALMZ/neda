using System;
using Microsoft.Extensions.Logging;
#nullable disable
namespace NEDA.Core.Logging
{
    /// <summary>
    /// NEDA genelinde kullanılan kısaltılmış log metotları.
    /// Info/Warning/Error/Debug çağrılarını standart ILogger'a map eder.
    /// </summary>
    public static class LoggerExtensions
    {
        public static void Info(this ILogger logger, string message) =>
            logger?.LogInformation(message);

        public static void Info(this ILogger logger, string message, Exception ex) =>
            logger?.LogInformation(ex, message);

        public static void Warning(this ILogger logger, string message) =>
            logger?.LogWarning(message);

        public static void Warning(this ILogger logger, string message, Exception ex) =>
            logger?.LogWarning(ex, message);

        public static void Error(this ILogger logger, string message) =>
            logger?.LogError(message);

        public static void Error(this ILogger logger, string message, Exception ex) =>
            logger?.LogError(ex, message);

        public static void Debug(this ILogger logger, string message) =>
            logger?.LogDebug(message);

        public static void Debug(this ILogger logger, string message, Exception ex) =>
            logger?.LogDebug(ex, message);
    }
}