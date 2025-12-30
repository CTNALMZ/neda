using Microsoft.Extensions.Logging;
using NEDA.Core.Logging;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace NEDA.Core.Common
{
    /// <summary>
    /// Provides utility methods for common operations
    /// </summary>
    public static class Utilities
    {
        private static readonly ILogger _logger = LogManager.CreateLogger(typeof(Utilities));
        private static readonly Random _random = new Random();
        private static readonly object _randomLock = new object();

        #region String Utilities

        /// <summary>
        /// Checks if a string is null, empty, or consists only of white-space characters
        /// </summary>
        public static bool IsNullOrWhiteSpace(this string value)
        {
            return string.IsNullOrWhiteSpace(value);
        }

        /// <summary>
        /// Checks if a string is not null, empty, or white-space
        /// </summary>
        public static bool IsNotNullOrWhiteSpace(this string value)
        {
            return !string.IsNullOrWhiteSpace(value);
        }

        /// <summary>
        /// Truncates a string to the specified maximum length
        /// </summary>
        public static string Truncate(this string value, int maxLength, string suffix = "...")
        {
            if (string.IsNullOrEmpty(value)) return value;
            if (maxLength <= 0) return string.Empty;

            return value.Length <= maxLength
                ? value
                : value.Substring(0, maxLength - suffix.Length) + suffix;
        }

        /// <summary>
        /// Converts a string to title case
        /// </summary>
        public static string ToTitleCase(this string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return value;

            var textInfo = CultureInfo.CurrentCulture.TextInfo;
            return textInfo.ToTitleCase(value.ToLower());
        }

        /// <summary>
        /// Removes all whitespace from a string
        /// </summary>
        public static string RemoveWhitespace(this string value)
        {
            if (string.IsNullOrEmpty(value))
                return value;

            return new string(value.Where(c => !char.IsWhiteSpace(c)).ToArray());
        }

        /// <summary>
        /// Converts a string to a URL-friendly slug
        /// </summary>
        public static string ToSlug(this string value, int maxLength = 100)
        {
            if (string.IsNullOrWhiteSpace(value))
                return string.Empty;

            var slug = value.ToLowerInvariant();

            // Replace Turkish characters
            slug = slug.Replace('ı', 'i')
                      .Replace('ğ', 'g')
                      .Replace('ü', 'u')
                      .Replace('ş', 's')
                      .Replace('ö', 'o')
                      .Replace('ç', 'c')
                      .Replace('İ', 'i')
                      .Replace('Ğ', 'g')
                      .Replace('Ü', 'u')
                      .Replace('Ş', 's')
                      .Replace('Ö', 'o')
                      .Replace('Ç', 'c');

            // Remove invalid characters
            slug = Regex.Replace(slug, @"[^a-z0-9\s-]", "");

            // Convert multiple spaces into one space
            slug = Regex.Replace(slug, @"\s+", " ").Trim();

            // Replace spaces with hyphens
            slug = Regex.Replace(slug, @"\s", "-");

            // Trim to max length
            if (slug.Length > maxLength)
            {
                slug = slug.Substring(0, maxLength).TrimEnd('-');
            }

            return slug;
        }

        /// <summary>
        /// Masks sensitive information in a string (like passwords or credit cards)
        /// </summary>
        public static string MaskSensitive(this string value, int visibleChars = 4, char maskChar = '*')
        {
            if (string.IsNullOrEmpty(value) || value.Length <= visibleChars)
                return new string(maskChar, Math.Max(value?.Length ?? 0, visibleChars));

            var maskedPart = new string(maskChar, value.Length - visibleChars);
            var visiblePart = value.Substring(value.Length - visibleChars);

            return maskedPart + visiblePart;
        }

        /// <summary>
        /// Generates a random string of specified length
        /// </summary>
        public static string GenerateRandomString(int length,
            string characterSet = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789")
        {
            if (length <= 0) return string.Empty;
            if (string.IsNullOrEmpty(characterSet))
                throw new ArgumentException("Character set cannot be null or empty", nameof(characterSet));

            lock (_randomLock)
            {
                return new string(Enumerable.Repeat(characterSet, length)
                    .Select(s => s[_random.Next(s.Length)]).ToArray());
            }
        }

        /// <summary>
        /// Converts a string to Base64
        /// </summary>
        public static string ToBase64(this string value)
        {
            if (string.IsNullOrEmpty(value)) return value;

            var bytes = Encoding.UTF8.GetBytes(value);
            return Convert.ToBase64String(bytes);
        }

        /// <summary>
        /// Converts a Base64 string back to original string
        /// </summary>
        public static string FromBase64(this string base64String)
        {
            if (string.IsNullOrEmpty(base64String)) return base64String;

            var bytes = Convert.FromBase64String(base64String);
            return Encoding.UTF8.GetString(bytes);
        }

        /// <summary>
        /// Computes MD5 hash of a string
        /// </summary>
        public static string ComputeMD5Hash(this string input)
        {
            if (string.IsNullOrEmpty(input)) return string.Empty;

            using (var md5 = MD5.Create())
            {
                var inputBytes = Encoding.UTF8.GetBytes(input);
                var hashBytes = md5.ComputeHash(inputBytes);

                var sb = new StringBuilder();
                foreach (var b in hashBytes)
                {
                    sb.Append(b.ToString("x2"));
                }
                return sb.ToString();
            }
        }

        /// <summary>
        /// Computes SHA256 hash of a string
        /// </summary>
        public static string ComputeSHA256Hash(this string input)
        {
            if (string.IsNullOrEmpty(input)) return string.Empty;

            using (var sha256 = SHA256.Create())
            {
                var inputBytes = Encoding.UTF8.GetBytes(input);
                var hashBytes = sha256.ComputeHash(inputBytes);

                var sb = new StringBuilder();
                foreach (var b in hashBytes)
                {
                    sb.Append(b.ToString("x2"));
                }
                return sb.ToString();
            }
        }

        #endregion

        #region Collection Utilities

        /// <summary>
        /// Checks if a collection is null or empty
        /// </summary>
        public static bool IsNullOrEmpty<T>(this IEnumerable<T> collection)
        {
            return collection == null || !collection.Any();
        }

        /// <summary>
        /// Checks if a collection is not null and has items
        /// </summary>
        public static bool IsNotNullOrEmpty<T>(this IEnumerable<T> collection)
        {
            return collection != null && collection.Any();
        }

        /// <summary>
        /// Converts a collection to a delimited string
        /// </summary>
        public static string ToDelimitedString<T>(this IEnumerable<T> collection, string delimiter = ", ")
        {
            if (collection == null) return string.Empty;

            return string.Join(delimiter, collection);
        }

        /// <summary>
        /// Splits a string and converts to a typed collection
        /// </summary>
        public static IEnumerable<T> FromDelimitedString<T>(this string value, string delimiter = ",",
            Func<string, T> converter = null)
        {
            if (string.IsNullOrWhiteSpace(value))
                return Enumerable.Empty<T>();

            var items = value.Split(new[] { delimiter }, StringSplitOptions.RemoveEmptyEntries);

            if (converter != null)
            {
                return items.Select(converter);
            }
            else
            {
                return items.Select(item => (T)Convert.ChangeType(item.Trim(), typeof(T)));
            }
        }

        /// <summary>
        /// Gets distinct elements by a property selector
        /// </summary>
        public static IEnumerable<TSource> DistinctBy<TSource, TKey>(
            this IEnumerable<TSource> source,
            Func<TSource, TKey> keySelector)
        {
            if (source == null) throw new ArgumentNullException(nameof(source));
            if (keySelector == null) throw new ArgumentNullException(nameof(keySelector));

            var seenKeys = new HashSet<TKey>();
            foreach (var element in source)
            {
                if (seenKeys.Add(keySelector(element)))
                {
                    yield return element;
                }
            }
        }

        /// <summary>
        /// Batches a collection into smaller chunks
        /// </summary>
        public static IEnumerable<IEnumerable<T>> Batch<T>(this IEnumerable<T> source, int batchSize)
        {
            if (source == null) throw new ArgumentNullException(nameof(source));
            if (batchSize <= 0) throw new ArgumentException("Batch size must be greater than 0", nameof(batchSize));

            var batch = new List<T>(batchSize);
            foreach (var item in source)
            {
                batch.Add(item);
                if (batch.Count == batchSize)
                {
                    yield return batch;
                    batch = new List<T>(batchSize);
                }
            }

            if (batch.Count > 0)
                yield return batch;
        }

        /// <summary>
        /// Safely gets a value from dictionary or returns default if key doesn't exist
        /// </summary>
        public static TValue GetValueOrDefault<TKey, TValue>(
            this IDictionary<TKey, TValue> dictionary,
            TKey key,
            TValue defaultValue = default)
        {
            if (dictionary == null) return defaultValue;
            return dictionary.TryGetValue(key, out var value) ? value : defaultValue;
        }

        #endregion

        #region DateTime Utilities

        /// <summary>
        /// Converts DateTime to Unix timestamp
        /// </summary>
        public static long ToUnixTimestamp(this DateTime dateTime)
        {
            var epoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            return (long)(dateTime.ToUniversalTime() - epoch).TotalSeconds;
        }

        /// <summary>
        /// Converts Unix timestamp to DateTime
        /// </summary>
        public static DateTime FromUnixTimestamp(this long timestamp)
        {
            var epoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            return epoch.AddSeconds(timestamp);
        }

        /// <summary>
        /// Gets the start of the day (00:00:00)
        /// </summary>
        public static DateTime StartOfDay(this DateTime dateTime)
        {
            return dateTime.Date;
        }

        /// <summary>
        /// Gets the end of the day (23:59:59.999)
        /// </summary>
        public static DateTime EndOfDay(this DateTime dateTime)
        {
            return dateTime.Date.AddDays(1).AddTicks(-1);
        }

        /// <summary>
        /// Gets the start of the week (Monday)
        /// </summary>
        public static DateTime StartOfWeek(this DateTime dateTime, DayOfWeek startOfWeek = DayOfWeek.Monday)
        {
            var diff = (7 + (dateTime.DayOfWeek - startOfWeek)) % 7;
            return dateTime.AddDays(-1 * diff).Date;
        }

        /// <summary>
        /// Gets the start of the month
        /// </summary>
        public static DateTime StartOfMonth(this DateTime dateTime)
        {
            return new DateTime(dateTime.Year, dateTime.Month, 1);
        }

        /// <summary>
        /// Gets the end of the month
        /// </summary>
        public static DateTime EndOfMonth(this DateTime dateTime)
        {
            return new DateTime(dateTime.Year, dateTime.Month, 1)
                .AddMonths(1)
                .AddDays(-1)
                .EndOfDay();
        }

        /// <summary>
        /// Formats a timespan to human readable format
        /// </summary>
        public static string ToHumanReadable(this TimeSpan timeSpan)
        {
            if (timeSpan.TotalDays >= 1)
                return $"{(int)timeSpan.TotalDays}d {timeSpan.Hours}h {timeSpan.Minutes}m";

            if (timeSpan.TotalHours >= 1)
                return $"{(int)timeSpan.TotalHours}h {timeSpan.Minutes}m {timeSpan.Seconds}s";

            if (timeSpan.TotalMinutes >= 1)
                return $"{(int)timeSpan.TotalMinutes}m {timeSpan.Seconds}s";

            if (timeSpan.TotalSeconds >= 1)
                return $"{timeSpan.TotalSeconds:F1}s";

            return $"{timeSpan.TotalMilliseconds}ms";
        }

        /// <summary>
        /// Gets relative time string (e.g., "2 hours ago")
        /// </summary>
        public static string ToRelativeTime(this DateTime dateTime)
        {
            var timeSpan = DateTime.UtcNow - dateTime.ToUniversalTime();

            if (timeSpan.TotalDays >= 365)
            {
                var years = (int)(timeSpan.TotalDays / 365);
                return $"{years} year{(years > 1 ? "s" : "")} ago";
            }

            if (timeSpan.TotalDays >= 30)
            {
                var months = (int)(timeSpan.TotalDays / 30);
                return $"{months} month{(months > 1 ? "s" : "")} ago";
            }

            if (timeSpan.TotalDays >= 7)
            {
                var weeks = (int)(timeSpan.TotalDays / 7);
                return $"{weeks} week{(weeks > 1 ? "s" : "")} ago";
            }

            if (timeSpan.TotalDays >= 1)
            {
                var days = (int)timeSpan.TotalDays;
                return $"{days} day{(days > 1 ? "s" : "")} ago";
            }

            if (timeSpan.TotalHours >= 1)
            {
                var hours = (int)timeSpan.TotalHours;
                return $"{hours} hour{(hours > 1 ? "s" : "")} ago";
            }

            if (timeSpan.TotalMinutes >= 1)
            {
                var minutes = (int)timeSpan.TotalMinutes;
                return $"{minutes} minute{(minutes > 1 ? "s" : "")} ago";
            }

            return "just now";
        }

        #endregion

        #region File and Path Utilities

        /// <summary>
        /// Safely combines path segments
        /// </summary>
        public static string CombinePaths(params string[] paths)
        {
            if (paths == null || paths.Length == 0)
                return string.Empty;

            var combined = Path.Combine(paths);
            return Path.GetFullPath(combined);
        }

        /// <summary>
        /// Creates directory if it doesn't exist
        /// </summary>
        public static void EnsureDirectoryExists(string directoryPath)
        {
            if (string.IsNullOrWhiteSpace(directoryPath))
                throw new ArgumentException("Directory path cannot be null or empty", nameof(directoryPath));

            if (!Directory.Exists(directoryPath))
            {
                Directory.CreateDirectory(directoryPath);
                _logger.LogDebug($"Created directory: {directoryPath}");
            }
        }

        /// <summary>
        /// Gets file size in human readable format
        /// </summary>
        public static string GetFileSizeReadable(long bytes)
        {
            string[] sizes = { "B", "KB", "MB", "GB", "TB" };
            double len = bytes;
            int order = 0;

            while (len >= 1024 && order < sizes.Length - 1)
            {
                order++;
                len = len / 1024;
            }

            return $"{len:0.##} {sizes[order]}";
        }

        /// <summary>
        /// Sanitizes a filename by removing invalid characters
        /// </summary>
        public static string SanitizeFileName(string fileName, string replacement = "_")
        {
            if (string.IsNullOrWhiteSpace(fileName))
                return fileName;

            var invalidChars = Path.GetInvalidFileNameChars();
            var sanitized = new string(fileName.Select(c => invalidChars.Contains(c) ? '_' : c).ToArray());

            // Remove multiple underscores
            sanitized = Regex.Replace(sanitized, @"_+", "_");

            return sanitized.Trim('_');
        }

        /// <summary>
        /// Gets file extension without the dot
        /// </summary>
        public static string GetFileExtension(string filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath))
                return string.Empty;

            return Path.GetExtension(filePath)?.TrimStart('.') ?? string.Empty;
        }

        /// <summary>
        /// Checks if a file is locked
        /// </summary>
        public static bool IsFileLocked(string filePath)
        {
            if (!File.Exists(filePath))
                return false;

            try
            {
                using (var stream = new FileStream(filePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                {
                    // File is not locked
                }
                return false;
            }
            catch (IOException)
            {
                return true;
            }
        }

        #endregion

        #region Validation Utilities

        /// <summary>
        /// Validates email address format
        /// </summary>
        public static bool IsValidEmail(this string email)
        {
            if (string.IsNullOrWhiteSpace(email))
                return false;

            try
            {
                var addr = new System.Net.Mail.MailAddress(email);
                return addr.Address == email;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Validates phone number format
        /// </summary>
        public static bool IsValidPhoneNumber(this string phoneNumber)
        {
            if (string.IsNullOrWhiteSpace(phoneNumber))
                return false;

            var digitsOnly = new string(phoneNumber.Where(char.IsDigit).ToArray());
            return digitsOnly.Length >= 10 && digitsOnly.Length <= 15;
        }

        /// <summary>
        /// Validates URL format
        /// </summary>
        public static bool IsValidUrl(this string url)
        {
            if (string.IsNullOrWhiteSpace(url))
                return false;

            return Uri.TryCreate(url, UriKind.Absolute, out var uriResult)
                   && (uriResult.Scheme == Uri.UriSchemeHttp || uriResult.Scheme == Uri.UriSchemeHttps);
        }

        /// <summary>
        /// Validates IP address format
        /// </summary>
        public static bool IsValidIpAddress(this string ipAddress)
        {
            if (string.IsNullOrWhiteSpace(ipAddress))
                return false;

            return System.Net.IPAddress.TryParse(ipAddress, out _);
        }

        /// <summary>
        /// Validates if string contains only letters and numbers
        /// </summary>
        public static bool IsAlphanumeric(this string input)
        {
            if (string.IsNullOrWhiteSpace(input))
                return false;

            return input.All(c => char.IsLetterOrDigit(c));
        }

        #endregion

        #region Math and Numeric Utilities

        /// <summary>
        /// Clamps a value between minimum and maximum
        /// </summary>
        public static T Clamp<T>(this T value, T min, T max) where T : IComparable<T>
        {
            if (min.CompareTo(max) > 0)
                throw new ArgumentException("Min cannot be greater than max");

            if (value.CompareTo(min) < 0)
                return min;

            if (value.CompareTo(max) > 0)
                return max;

            return value;
        }

        /// <summary>
        /// Maps a value from one range to another
        /// </summary>
        public static double MapRange(double value, double fromMin, double fromMax, double toMin, double toMax)
        {
            if (Math.Abs(fromMax - fromMin) < double.Epsilon)
                return toMin;

            return ((value - fromMin) / (fromMax - fromMin)) * (toMax - toMin) + toMin;
        }

        /// <summary>
        /// Calculates percentage
        /// </summary>
        public static double CalculatePercentage(double value, double total)
        {
            if (Math.Abs(total) < double.Epsilon)
                return 0;

            return (value / total) * 100;
        }

        /// <summary>
        /// Generates a random number within range
        /// </summary>
        public static int RandomInt(int min, int max)
        {
            lock (_randomLock)
            {
                return _random.Next(min, max + 1);
            }
        }

        /// <summary>
        /// Generates a random double within range
        /// </summary>
        public static double RandomDouble(double min, double max)
        {
            lock (_randomLock)
            {
                return min + (_random.NextDouble() * (max - min));
            }
        }

        /// <summary>
        /// Generates a random boolean
        /// </summary>
        public static bool RandomBool()
        {
            lock (_randomLock)
            {
                return _random.Next(2) == 0;
            }
        }

        #endregion

        #region Async and Task Utilities

        /// <summary>
        /// Safely executes an async method with timeout
        /// </summary>
        public static async Task<T> WithTimeout<T>(this Task<T> task, TimeSpan timeout,
            CancellationToken cancellationToken = default)
        {
            using (var timeoutCancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                var completedTask = await Task.WhenAny(task, Task.Delay(timeout, timeoutCancellationTokenSource.Token));

                if (completedTask == task)
                {
                    timeoutCancellationTokenSource.Cancel();
                    return await task;
                }

                throw new TimeoutException($"Operation timed out after {timeout.TotalSeconds} seconds");
            }
        }

        /// <summary>
        /// Retries an async operation with exponential backoff
        /// </summary>
        public static async Task<T> RetryAsync<T>(
            Func<Task<T>> operation,
            int maxRetries = 3,
            TimeSpan initialDelay = default,
            Func<Exception, bool> shouldRetry = null)
        {
            if (initialDelay == default)
                initialDelay = TimeSpan.FromSeconds(1);

            var exceptions = new List<Exception>();

            for (int retryCount = 0; retryCount <= maxRetries; retryCount++)
            {
                try
                {
                    return await operation();
                }
                catch (Exception ex)
                {
                    exceptions.Add(ex);

                    if (shouldRetry != null && !shouldRetry(ex))
                        throw new AggregateException("Operation failed after retries", exceptions);

                    if (retryCount == maxRetries)
                        throw new AggregateException($"Operation failed after {maxRetries} retries", exceptions);

                    var delay = TimeSpan.FromSeconds(Math.Pow(2, retryCount)) * initialDelay;
                    _logger.LogWarning($"Retry {retryCount + 1}/{maxRetries} after error: {ex.Message}. Waiting {delay.TotalSeconds}s");

                    await Task.Delay(delay);
                }
            }

            throw new InvalidOperationException("Unreachable code reached in RetryAsync");
        }

        /// <summary>
        /// Executes async operation with cancellation token fallback
        /// </summary>
        public static async Task<T> WithCancellation<T>(this Task<T> task, CancellationToken cancellationToken)
        {
            var tcs = new TaskCompletionSource<bool>();

            using (cancellationToken.Register(s => ((TaskCompletionSource<bool>)s).TrySetResult(true), tcs))
            {
                if (task != await Task.WhenAny(task, tcs.Task))
                {
                    throw new OperationCanceledException(cancellationToken);
                }
            }

            return await task;
        }

        /// <summary>
        /// Executes async operation with progress reporting
        /// </summary>
        public static async Task<T> WithProgress<T>(this Task<T> task, IProgress<double> progress)
        {
            var progressTask = Task.Run(async () =>
            {
                while (!task.IsCompleted)
                {
                    progress?.Report(0.5);
                    await Task.Delay(100);
                }

                progress?.Report(1.0);
            });

            var result = await task;
            await progressTask;

            return result;
        }

        #endregion

        #region Type and Reflection Utilities

        /// <summary>
        /// Gets default value for a type
        /// </summary>
        public static object GetDefaultValue(Type type)
        {
            if (type == null)
                throw new ArgumentNullException(nameof(type));

            return type.IsValueType ? Activator.CreateInstance(type) : null;
        }

        /// <summary>
        /// Checks if type is numeric
        /// </summary>
        public static bool IsNumericType(this Type type)
        {
            if (type == null) return false;

            var numericTypes = new HashSet<Type>
            {
                typeof(byte), typeof(sbyte), typeof(short), typeof(ushort),
                typeof(int), typeof(uint), typeof(long), typeof(ulong),
                typeof(float), typeof(double), typeof(decimal)
            };

            var underlying = Nullable.GetUnderlyingType(type) ?? type;
            return numericTypes.Contains(underlying);
        }

        /// <summary>
        /// Creates instance of type with default constructor
        /// </summary>
        public static object CreateInstance(Type type, params object[] args)
        {
            if (type == null)
                throw new ArgumentNullException(nameof(type));

            try
            {
                return Activator.CreateInstance(type, args);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to create instance of type {type.Name}");
                throw new TypeInitializationException(type.FullName, ex);
            }
        }

        /// <summary>
        /// Gets all types that implement an interface
        /// </summary>
        public static IEnumerable<Type> GetImplementingTypes<TInterface>(Assembly assembly = null)
        {
            var interfaceType = typeof(TInterface);
            if (!interfaceType.IsInterface)
                throw new ArgumentException("Type must be an interface", nameof(TInterface));

            var assemblies = assembly != null
                ? new[] { assembly }
                : AppDomain.CurrentDomain.GetAssemblies();

            return assemblies
                .SelectMany(a => a.GetTypes())
                .Where(t => interfaceType.IsAssignableFrom(t) && !t.IsInterface && !t.IsAbstract);
        }

        #endregion

        #region Performance and Memory Utilities

        /// <summary>
        /// Measures execution time of an action
        /// </summary>
        public static TimeSpan MeasureExecutionTime(Action action)
        {
            if (action == null)
                throw new ArgumentNullException(nameof(action));

            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            action();
            stopwatch.Stop();

            return stopwatch.Elapsed;
        }

        /// <summary>
        /// Measures execution time of an async function
        /// </summary>
        public static async Task<TimeSpan> MeasureExecutionTimeAsync(Func<Task> asyncAction)
        {
            if (asyncAction == null)
                throw new ArgumentNullException(nameof(asyncAction));

            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            await asyncAction();
            stopwatch.Stop();

            return stopwatch.Elapsed;
        }

        /// <summary>
        /// Gets current memory usage
        /// </summary>
        public static long GetCurrentMemoryUsage()
        {
            return GC.GetTotalMemory(false);
        }

        /// <summary>
        /// Forces garbage collection (use cautiously)
        /// </summary>
        public static void ForceGarbageCollection()
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
        }

        #endregion

        #region Miscellaneous Utilities

        /// <summary>
        /// Deep clones an object using serialization (object must be serializable)
        /// </summary>
        public static T DeepClone<T>(T obj)
        {
            if (obj == null) return default;

            using (var ms = new MemoryStream())
            {
                var formatter = new System.Runtime.Serialization.Formatters.Binary.BinaryFormatter();
#pragma warning disable SYSLIB0011
                formatter.Serialize(ms, obj);
                ms.Seek(0, SeekOrigin.Begin);
                return (T)formatter.Deserialize(ms);
#pragma warning restore SYSLIB0011
            }
        }

        /// <summary>
        /// Generates a unique identifier
        /// </summary>
        public static string GenerateUniqueId()
        {
            return Guid.NewGuid().ToString("N");
        }

        /// <summary>
        /// Creates a temporary file with specified content
        /// </summary>
        public static string CreateTempFile(string content = null, string extension = ".tmp")
        {
            var tempPath = Path.GetTempPath();
            var fileName = Path.Combine(tempPath, $"{Guid.NewGuid()}{extension}");

            if (content != null)
            {
                File.WriteAllText(fileName, content);
            }
            else
            {
                File.Create(fileName).Close();
            }

            return fileName;
        }

        /// <summary>
        /// Disposes an object safely
        /// </summary>
        public static void SafeDispose(IDisposable disposable)
        {
            if (disposable != null)
            {
                try
                {
                    disposable.Dispose();
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Error disposing object");
                }
            }
        }

        /// <summary>
        /// Executes action with a temporary context
        /// </summary>
        public static void Using<T>(T resource, Action<T> action) where T : IDisposable
        {
            if (resource == null) throw new ArgumentNullException(nameof(resource));
            if (action == null) throw new ArgumentNullException(nameof(action));

            using (resource)
            {
                action(resource);
            }
        }

        /// <summary>
        /// Executes async function with a temporary context
        /// </summary>
        public static async Task UsingAsync<T>(T resource, Func<T, Task> asyncAction) where T : IDisposable
        {
            if (resource == null) throw new ArgumentNullException(nameof(resource));
            if (asyncAction == null) throw new ArgumentNullException(nameof(asyncAction));

            using (resource)
            {
                await asyncAction(resource);
            }
        }

        #endregion
    }
}
