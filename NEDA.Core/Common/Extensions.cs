using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;
using System.Xml.Linq;

namespace NEDA.Core.Common.Extensions
{
    /// <summary>
    /// Provides comprehensive extension methods for various .NET types;
    /// </summary>
    public static class Extensions
    {
        #region Helper enums

        public enum DuplicateKeyHandling
        {
            KeepFirst,
            KeepLast,
            ThrowException,
            AggregateToList
        }

        #endregion

        #region String Extensions

        private static readonly Regex EmailRegex = new Regex(
            @"^[^@\s]+@[^@\s]+\.[^@\s]+$",
            RegexOptions.Compiled | RegexOptions.IgnoreCase
        );

        private static readonly Regex PhoneRegex = new Regex(
            @"^[\+]?[1-9][\d]{0,15}$",
            RegexOptions.Compiled
        );

        private static readonly Regex UrlRegex = new Regex(
            @"^(https?|ftp)://[^\s/$.?#].[^\s]*$",
            RegexOptions.Compiled | RegexOptions.IgnoreCase
        );

        private static readonly Regex HtmlRegex = new Regex(
            @"<[^>]*>",
            RegexOptions.Compiled
        );

        private static readonly Regex WhitespaceRegex = new Regex(
            @"\s+",
            RegexOptions.Compiled
        );

        /// <summary>
        /// Determines whether a string is null, empty, or consists only of white-space characters;
        /// </summary>
        public static bool IsNullOrWhiteSpace(this string value)
        {
            return string.IsNullOrWhiteSpace(value);
        }

        /// <summary>
        /// Determines whether a string is not null, empty, or consists only of white-space characters;
        /// </summary>
        public static bool IsNotNullOrWhiteSpace(this string value)
        {
            return !string.IsNullOrWhiteSpace(value);
        }

        /// <summary>
        /// Returns the string value or empty string if null;
        /// </summary>
        public static string OrEmpty(this string value)
        {
            return value ?? string.Empty;
        }

        /// <summary>
        /// Returns the string value or default value if null/empty;
        /// </summary>
        public static string OrDefault(this string value, string defaultValue)
        {
            return string.IsNullOrWhiteSpace(value) ? defaultValue : value;
        }

        /// <summary>
        /// Truncates a string to the specified maximum length;
        /// </summary>
        public static string Truncate(this string value, int maxLength, string suffix = "...")
        {
            if (string.IsNullOrEmpty(value) || value.Length <= maxLength)
                return value;

            if (maxLength <= suffix.Length)
                return suffix;

            return value.Substring(0, maxLength - suffix.Length) + suffix;
        }

        /// <summary>
        /// Truncates a string to the specified maximum length from the end;
        /// </summary>
        public static string TruncateFromEnd(this string value, int maxLength, string prefix = "...")
        {
            if (string.IsNullOrEmpty(value) || value.Length <= maxLength)
                return value;

            if (maxLength <= prefix.Length)
                return prefix;

            return prefix + value.Substring(value.Length - maxLength + prefix.Length);
        }

        /// <summary>
        /// Converts string to title case using specified culture;
        /// </summary>
        public static string ToTitleCase(this string value, CultureInfo culture = null)
        {
            if (string.IsNullOrWhiteSpace(value))
                return value;

            culture ??= CultureInfo.CurrentCulture;
            return culture.TextInfo.ToTitleCase(value.ToLower(culture));
        }

        /// <summary>
        /// Removes all whitespace from a string;
        /// </summary>
        public static string RemoveWhitespace(this string value)
        {
            if (string.IsNullOrEmpty(value))
                return value;

            return WhitespaceRegex.Replace(value, string.Empty);
        }

        /// <summary>
        /// Compares two strings using ordinal ignore case;
        /// </summary>
        public static bool EqualsIgnoreCase(this string value, string compareTo)
        {
            return string.Equals(value, compareTo, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Determines whether a string contains a substring using ordinal ignore case;
        /// </summary>
        public static bool ContainsIgnoreCase(this string source, string value)
        {
            if (source == null || value == null)
                return false;

            return source.IndexOf(value, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        /// <summary>
        /// Determines whether a string starts with a substring using ordinal ignore case;
        /// </summary>
        public static bool StartsWithIgnoreCase(this string source, string value)
        {
            if (source == null || value == null)
                return false;

            return source.StartsWith(value, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Determines whether a string ends with a substring using ordinal ignore case;
        /// </summary>
        public static bool EndsWithIgnoreCase(this string source, string value)
        {
            if (source == null || value == null)
                return false;

            return source.EndsWith(value, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Converts a string to a secure SHA256 hash;
        /// </summary>
        public static string ToSha256(this string value)
        {
            if (string.IsNullOrEmpty(value))
                return string.Empty;

            using var sha256 = SHA256.Create();
            var bytes = sha256.ComputeHash(Encoding.UTF8.GetBytes(value));
            return Convert.ToBase64String(bytes);
        }

        /// <summary>
        /// Converts a string to a secure MD5 hash;
        /// </summary>
        public static string ToMd5(this string value)
        {
            if (string.IsNullOrEmpty(value))
                return string.Empty;

            using var md5 = MD5.Create();
            var bytes = md5.ComputeHash(Encoding.UTF8.GetBytes(value));
            return BitConverter.ToString(bytes).Replace("-", "").ToLowerInvariant();
        }

        /// <summary>
        /// Validates if string is a valid email address;
        /// </summary>
        public static bool IsValidEmail(this string value)
        {
            return !string.IsNullOrWhiteSpace(value) && EmailRegex.IsMatch(value);
        }

        /// <summary>
        /// Validates if string is a valid phone number;
        /// </summary>
        public static bool IsValidPhone(this string value)
        {
            return !string.IsNullOrWhiteSpace(value) && PhoneRegex.IsMatch(value);
        }

        /// <summary>
        /// Validates if string is a valid URL;
        /// </summary>
        public static bool IsValidUrl(this string value)
        {
            return !string.IsNullOrWhiteSpace(value) &&
                   Uri.TryCreate(value, UriKind.Absolute, out var uriResult) &&
                   (uriResult.Scheme == Uri.UriSchemeHttp || uriResult.Scheme == Uri.UriSchemeHttps);
        }

        /// <summary>
        /// Strips HTML tags from a string;
        /// </summary>
        public static string StripHtml(this string value)
        {
            if (string.IsNullOrEmpty(value))
                return value;

            return HtmlRegex.Replace(value, string.Empty);
        }

        /// <summary>
        /// Converts a string to Base64 encoding;
        /// </summary>
        public static string ToBase64(this string value)
        {
            if (string.IsNullOrEmpty(value))
                return string.Empty;

            var bytes = Encoding.UTF8.GetBytes(value);
            return Convert.ToBase64String(bytes);
        }

        /// <summary>
        /// Decodes a Base64 string;
        /// </summary>
        public static string FromBase64(this string value)
        {
            if (string.IsNullOrEmpty(value))
                return string.Empty;

            try
            {
                var bytes = Convert.FromBase64String(value);
                return Encoding.UTF8.GetString(bytes);
            }
            catch
            {
                return string.Empty;
            }
        }

        /// <summary>
        /// Safely converts a string to integer with default fallback;
        /// </summary>
        public static int ToInt(this string value, int defaultValue = 0)
        {
            return int.TryParse(value, out var result) ? result : defaultValue;
        }

        /// <summary>
        /// Safely converts a string to long with default fallback;
        /// </summary>
        public static long ToLong(this string value, long defaultValue = 0)
        {
            return long.TryParse(value, out var result) ? result : defaultValue;
        }

        /// <summary>
        /// Safely converts a string to decimal with default fallback;
        /// </summary>
        public static decimal ToDecimal(this string value, decimal defaultValue = 0)
        {
            return decimal.TryParse(value, out var result) ? result : defaultValue;
        }

        /// <summary>
        /// Safely converts a string to double with default fallback;
        /// </summary>
        public static double ToDouble(this string value, double defaultValue = 0)
        {
            return double.TryParse(value, out var result) ? result : defaultValue;
        }

        /// <summary>
        /// Safely converts a string to boolean with default fallback;
        /// </summary>
        public static bool ToBool(this string value, bool defaultValue = false)
        {
            if (string.IsNullOrWhiteSpace(value))
                return defaultValue;

            if (bool.TryParse(value, out var result))
                return result;

            var lower = value.Trim().ToLowerInvariant();
            return lower switch
            {
                "1" => true,
                "0" => false,
                "yes" => true,
                "no" => false,
                "on" => true,
                "off" => false,
                "true" => true,
                "false" => false,
                _ => defaultValue
            };
        }

        /// <summary>
        /// Safely converts a string to DateTime with default fallback;
        /// </summary>
        public static DateTime ToDateTime(this string value, DateTime? defaultValue = null)
        {
            return DateTime.TryParse(value, out var result) ? result : defaultValue ?? DateTime.MinValue;
        }

        /// <summary>
        /// Converts a string to camelCase;
        /// </summary>
        public static string ToCamelCase(this string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return value;

            if (value.Length == 1)
                return value.ToLowerInvariant();

            return char.ToLowerInvariant(value[0]) + value.Substring(1);
        }

        /// <summary>
        /// Converts a string to PascalCase;
        /// </summary>
        public static string ToPascalCase(this string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return value;

            if (value.Length == 1)
                return value.ToUpperInvariant();

            return char.ToUpperInvariant(value[0]) + value.Substring(1);
        }

        /// <summary>
        /// Converts a string to kebab-case;
        /// </summary>
        public static string ToKebabCase(this string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return value;

            var result = new StringBuilder();
            var previousChar = char.MinValue;

            foreach (var c in value)
            {
                if (char.IsUpper(c))
                {
                    if (result.Length > 0 && previousChar != '-')
                        result.Append('-');
                    result.Append(char.ToLowerInvariant(c));
                }
                else if (char.IsLetterOrDigit(c))
                {
                    result.Append(c);
                }
                else if (c == ' ' || c == '_')
                {
                    if (result.Length > 0 && previousChar != '-')
                        result.Append('-');
                }

                previousChar = c;
            }

            return result.ToString();
        }

        /// <summary>
        /// Converts a string to snake_case;
        /// </summary>
        public static string ToSnakeCase(this string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return value;

            var result = new StringBuilder();
            var previousChar = char.MinValue;

            foreach (var c in value)
            {
                if (char.IsUpper(c))
                {
                    if (result.Length > 0 && previousChar != '_')
                        result.Append('_');
                    result.Append(char.ToLowerInvariant(c));
                }
                else if (char.IsLetterOrDigit(c))
                {
                    result.Append(c);
                }
                else if (c == ' ' || c == '-')
                {
                    if (result.Length > 0 && previousChar != '_')
                        result.Append('_');
                }

                previousChar = c;
            }

            return result.ToString();
        }

        /// <summary>
        /// Extracts numbers from a string;
        /// </summary>
        public static string ExtractNumbers(this string value)
        {
            if (string.IsNullOrEmpty(value))
                return string.Empty;

            return new string(value.Where(char.IsDigit).ToArray());
        }

        /// <summary>
        /// Extracts letters from a string;
        /// </summary>
        public static string ExtractLetters(this string value)
        {
            if (string.IsNullOrEmpty(value))
                return string.Empty;

            return new string(value.Where(char.IsLetter).ToArray());
        }

        /// <summary>
        /// Masks sensitive information in a string (e.g., email, credit card)
        /// </summary>
        public static string MaskSensitive(this string value, char maskChar = '*', int visibleChars = 4)
        {
            if (string.IsNullOrEmpty(value) || value.Length <= visibleChars)
                return new string(maskChar, value?.Length ?? 0);

            var maskedLength = value.Length - visibleChars;
            return new string(maskChar, maskedLength) + value.Substring(maskedLength);
        }

        /// <summary>
        /// Reverses a string;
        /// </summary>
        public static string Reverse(this string value)
        {
            if (string.IsNullOrEmpty(value))
                return value;

            var charArray = value.ToCharArray();
            Array.Reverse(charArray);
            return new string(charArray);
        }

        /// <summary>
        /// Counts words in a string;
        /// </summary>
        public static int WordCount(this string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return 0;

            return WhitespaceRegex.Split(value.Trim()).Length;
        }

        /// <summary>
        /// Ensures a string ends with specified suffix;
        /// </summary>
        public static string EnsureEndsWith(this string value, string suffix)
        {
            if (string.IsNullOrEmpty(value))
                return suffix;

            return value.EndsWith(suffix) ? value : value + suffix;
        }

        /// <summary>
        /// Ensures a string starts with specified prefix;
        /// </summary>
        public static string EnsureStartsWith(this string value, string prefix)
        {
            if (string.IsNullOrEmpty(value))
                return prefix;

            return value.StartsWith(prefix) ? value : prefix + value;
        }

        /// <summary>
        /// Removes specified suffix from string if present;
        /// </summary>
        public static string RemoveSuffix(this string value, string suffix)
        {
            if (string.IsNullOrEmpty(value) || string.IsNullOrEmpty(suffix))
                return value;

            return value.EndsWith(suffix) ? value.Substring(0, value.Length - suffix.Length) : value;
        }

        /// <summary>
        /// Removes specified prefix from string if present;
        /// </summary>
        public static string RemovePrefix(this string value, string prefix)
        {
            if (string.IsNullOrEmpty(value) || string.IsNullOrEmpty(prefix))
                return value;

            return value.StartsWith(prefix) ? value.Substring(prefix.Length) : value;
        }

        /// <summary>
        /// Splits a string by capital letters;
        /// </summary>
        public static string SplitCamelCase(this string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return value;

            return Regex.Replace(value, "([a-z])([A-Z])", "$1 $2");
        }

        #endregion

        #region Collection Extensions

        public static bool IsNullOrEmpty<T>(this IEnumerable<T> collection)
        {
            return collection == null || !collection.Any();
        }

        public static bool IsNotNullOrEmpty<T>(this IEnumerable<T> collection)
        {
            return collection != null && collection.Any();
        }

        public static void ForEach<T>(this IEnumerable<T> collection, Action<T> action)
        {
            if (collection == null || action == null)
                return;

            foreach (var item in collection)
            {
                action(item);
            }
        }

        public static async Task ForEachAsync<T>(this IEnumerable<T> collection, Func<T, Task> asyncAction)
        {
            if (collection == null || asyncAction == null)
                return;

            foreach (var item in collection)
            {
                await asyncAction(item);
            }
        }

        public static string ToDelimitedString<T>(this IEnumerable<T> collection, string delimiter = ", ")
        {
            if (collection == null)
                return string.Empty;

            return string.Join(delimiter, collection);
        }

        public static string ToDelimitedString<T>(this IEnumerable<T> collection, Func<T, string> selector, string delimiter = ", ")
        {
            if (collection == null || selector == null)
                return string.Empty;

            return string.Join(delimiter, collection.Select(selector));
        }

        public static IEnumerable<T> DistinctBy<T, TKey>(this IEnumerable<T> collection, Func<T, TKey> keySelector)
        {
            if (collection == null || keySelector == null)
                return Enumerable.Empty<T>();

            var seenKeys = new HashSet<TKey>();
            foreach (var element in collection)
            {
                if (seenKeys.Add(keySelector(element)))
                    yield return element;
            }
        }

        public static HashSet<T> ToHashSet<T>(this IEnumerable<T> collection, IEqualityComparer<T> comparer = null)
        {
            if (collection == null)
                return comparer == null ? new HashSet<T>() : new HashSet<T>(comparer);

            return comparer == null
                ? new HashSet<T>(collection)
                : new HashSet<T>(collection, comparer);
        }

        public static IEnumerable<T> Page<T>(this IEnumerable<T> collection, int pageNumber, int pageSize)
        {
            if (collection == null)
                return Enumerable.Empty<T>();

            if (pageNumber < 1) pageNumber = 1;
            if (pageSize < 1) pageSize = 10;

            return collection
                .Skip((pageNumber - 1) * pageSize)
                .Take(pageSize);
        }

        public static bool ContainsAny<T>(this IEnumerable<T> collection, params T[] values)
        {
            if (collection == null || values == null)
                return false;

            return collection.Any(values.Contains);
        }

        public static bool ContainsAll<T>(this IEnumerable<T> collection, params T[] values)
        {
            if (collection == null || values == null)
                return false;

            return values.All(collection.Contains);
        }

        public static T ElementAtOrDefault<T>(this IList<T> list, int index, T defaultValue = default)
        {
            if (list == null || index < 0 || index >= list.Count)
                return defaultValue;

            return list[index];
        }

        public static void AddRange<T>(this ICollection<T> collection, IEnumerable<T> items)
        {
            if (collection == null || items == null)
                return;

            foreach (var item in items)
            {
                collection.Add(item);
            }
        }

        public static Dictionary<TKey, TValue> ToDictionarySafe<TSource, TKey, TValue>(
            this IEnumerable<TSource> source,
            Func<TSource, TKey> keySelector,
            Func<TSource, TValue> valueSelector,
            DuplicateKeyHandling handling = DuplicateKeyHandling.KeepFirst)
        {
            var dictionary = new Dictionary<TKey, TValue>();

            if (source == null || keySelector == null || valueSelector == null)
                return dictionary;

            foreach (var item in source)
            {
                var key = keySelector(item);
                var value = valueSelector(item);

                if (dictionary.ContainsKey(key))
                {
                    switch (handling)
                    {
                        case DuplicateKeyHandling.KeepFirst:
                            continue;

                        case DuplicateKeyHandling.KeepLast:
                            dictionary[key] = value;
                            break;

                        case DuplicateKeyHandling.ThrowException:
                            throw new ArgumentException($"Duplicate key found: {key}");

                        case DuplicateKeyHandling.AggregateToList:
                            if (dictionary[key] is List<TValue> list)
                            {
                                list.Add(value);
                            }
                            else
                            {
                                var newList = new List<TValue> { dictionary[key], value };
                                dictionary[key] = (TValue)(object)newList;
                            }
                            break;
                    }
                }
                else
                {
                    dictionary.Add(key, value);
                }
            }

            return dictionary;
        }

        public static IEnumerable<T> Shuffle<T>(this IEnumerable<T> collection)
        {
            if (collection == null)
                return Enumerable.Empty<T>();

            var list = collection.ToList();
            var rng = new Random();
            var n = list.Count;

            while (n > 1)
            {
                n--;
                var k = rng.Next(n + 1);
                (list[k], list[n]) = (list[n], list[k]);
            }

            return list;
        }

        public static int FindIndex<T>(this IEnumerable<T> collection, Func<T, bool> predicate)
        {
            if (collection == null || predicate == null)
                return -1;

            var index = 0;
            foreach (var item in collection)
            {
                if (predicate(item))
                    return index;
                index++;
            }

            return -1;
        }

        public static IEnumerable<int> FindIndices<T>(this IEnumerable<T> collection, Func<T, bool> predicate)
        {
            if (collection == null || predicate == null)
                yield break;

            var index = 0;
            foreach (var item in collection)
            {
                if (predicate(item))
                    yield return index;
                index++;
            }
        }

        #endregion

        #region DateTime Extensions

        public static long ToUnixTimestamp(this DateTime dateTime)
        {
            var epoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            return (long)(dateTime.ToUniversalTime() - epoch).TotalSeconds;
        }

        public static long ToUnixTimestampMilliseconds(this DateTime dateTime)
        {
            var epoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            return (long)(dateTime.ToUniversalTime() - epoch).TotalMilliseconds;
        }

        public static DateTime FromUnixTimestamp(this long timestamp)
        {
            var epoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            return epoch.AddSeconds(timestamp);
        }

        public static DateTime FromUnixTimestampMilliseconds(this long timestamp)
        {
            var epoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            return epoch.AddMilliseconds(timestamp);
        }

        public static DateTime StartOfDay(this DateTime dateTime)
        {
            return new DateTime(dateTime.Year, dateTime.Month, dateTime.Day, 0, 0, 0, 0, dateTime.Kind);
        }

        public static DateTime EndOfDay(this DateTime dateTime)
        {
            return new DateTime(dateTime.Year, dateTime.Month, dateTime.Day, 23, 59, 59, 999, dateTime.Kind);
        }

        public static DateTime StartOfWeek(this DateTime dateTime, DayOfWeek startOfWeek = DayOfWeek.Monday)
        {
            var diff = (7 + (dateTime.DayOfWeek - startOfWeek)) % 7;
            return dateTime.AddDays(-1 * diff).StartOfDay();
        }

        public static DateTime EndOfWeek(this DateTime dateTime, DayOfWeek startOfWeek = DayOfWeek.Monday)
        {
            var start = dateTime.StartOfWeek(startOfWeek);
            return start.AddDays(6).EndOfDay();
        }

        public static DateTime StartOfMonth(this DateTime dateTime)
        {
            return new DateTime(dateTime.Year, dateTime.Month, 1, 0, 0, 0, 0, dateTime.Kind);
        }

        public static DateTime EndOfMonth(this DateTime dateTime)
        {
            return new DateTime(
                dateTime.Year,
                dateTime.Month,
                DateTime.DaysInMonth(dateTime.Year, dateTime.Month),
                23, 59, 59, 999,
                dateTime.Kind);
        }

        public static DateTime StartOfYear(this DateTime dateTime)
        {
            return new DateTime(dateTime.Year, 1, 1, 0, 0, 0, 0, dateTime.Kind);
        }

        public static DateTime EndOfYear(this DateTime dateTime)
        {
            return new DateTime(dateTime.Year, 12, 31, 23, 59, 59, 999, dateTime.Kind);
        }

        public static int GetAge(this DateTime birthDate, DateTime? referenceDate = null)
        {
            var today = referenceDate ?? DateTime.Today;
            var age = today.Year - birthDate.Year;

            if (birthDate.Date > today.AddYears(-age))
                age--;

            return age;
        }

        public static bool IsBetween(this DateTime dateTime, DateTime start, DateTime end)
        {
            return dateTime >= start && dateTime <= end;
        }

        public static bool IsToday(this DateTime dateTime)
        {
            return dateTime.Date == DateTime.Today;
        }

        public static bool IsFuture(this DateTime dateTime)
        {
            return dateTime > DateTime.Now;
        }

        public static bool IsPast(this DateTime dateTime)
        {
            return dateTime < DateTime.Now;
        }

        /// <summary>
        /// Gets a friendly relative time string (e.g., "2 hours ago")
        /// </summary>
        public static string ToRelativeTime(this DateTime dateTime)
        {
            var timeSpan = DateTime.Now - dateTime;

            if (timeSpan.TotalMinutes < 1)
                return "just now";

            if (timeSpan.TotalMinutes < 60)
                return $"{(int)timeSpan.TotalMinutes} minute{(timeSpan.TotalMinutes >= 2 ? "s" : "")} ago";

            if (timeSpan.TotalHours < 24)
                return $"{(int)timeSpan.TotalHours} hour{(timeSpan.TotalHours >= 2 ? "s" : "")} ago";

            if (timeSpan.TotalDays < 7)
                return $"{(int)timeSpan.TotalDays} day{(timeSpan.TotalDays >= 2 ? "s" : "")} ago";

            if (timeSpan.TotalDays < 30)
                return $"{(int)(timeSpan.TotalDays / 7)} week{((int)(timeSpan.TotalDays / 7) >= 2 ? "s" : "")} ago";

            if (timeSpan.TotalDays < 365)
                return $"{(int)(timeSpan.TotalDays / 30)} month{((int)(timeSpan.TotalDays / 30) >= 2 ? "s" : "")} ago";

            return $"{(int)(timeSpan.TotalDays / 365)} year{((int)(timeSpan.TotalDays / 365) >= 2 ? "s" : "")} ago";
        }

        public static int GetQuarter(this DateTime dateTime)
        {
            return (dateTime.Month - 1) / 3 + 1;
        }

        public static int GetBusinessDays(this DateTime start, DateTime end)
        {
            var totalDays = (int)(end - start).TotalDays;
            var businessDays = 0;

            for (var i = 0; i <= totalDays; i++)
            {
                var date = start.AddDays(i);
                if (date.DayOfWeek != DayOfWeek.Saturday && date.DayOfWeek != DayOfWeek.Sunday)
                    businessDays++;
            }

            return businessDays;
        }

        public static DateTime AddBusinessDays(this DateTime date, int days)
        {
            var sign = Math.Sign(days);
            var unsignedDays = Math.Abs(days);
            var result = date;

            while (unsignedDays > 0)
            {
                result = result.AddDays(sign);
                if (result.DayOfWeek != DayOfWeek.Saturday && result.DayOfWeek != DayOfWeek.Sunday)
                    unsignedDays--;
            }

            return result;
        }

        #endregion

        #region Numeric Extensions

        public static bool IsBetween(this int value, int min, int max)
        {
            return value >= min && value <= max;
        }

        public static bool IsBetween(this double value, double min, double max)
        {
            return value >= min && value <= max;
        }

        public static bool IsBetween(this decimal value, decimal min, decimal max)
        {
            return value >= min && value <= max;
        }

        public static int Clamp(this int value, int min, int max)
        {
            if (value < min) return min;
            if (value > max) return max;
            return value;
        }

        public static double Clamp(this double value, double min, double max)
        {
            if (value < min) return min;
            if (value > max) return max;
            return value;
        }

        public static decimal Clamp(this decimal value, decimal min, decimal max)
        {
            if (value < min) return min;
            if (value > max) return max;
            return value;
        }

        public static string ToFileSize(this long bytes)
        {
            string[] sizes = { "B", "KB", "MB", "GB", "TB", "PB", "EB" };

            if (bytes == 0)
                return "0 B";

            var i = (int)Math.Floor(Math.Log(bytes) / Math.Log(1024));
            return $"{(bytes / Math.Pow(1024, i)):0.##} {sizes[i]}";
        }

        public static double ToRadians(this double degrees)
        {
            return degrees * Math.PI / 180.0;
        }

        public static double ToDegrees(this double radians)
        {
            return radians * 180.0 / Math.PI;
        }

        public static double RoundTo(this double value, int decimalPlaces)
        {
            return Math.Round(value, decimalPlaces);
        }

        public static decimal RoundTo(this decimal value, int decimalPlaces)
        {
            return Math.Round(value, decimalPlaces);
        }

        public static double PercentageOf(this double value, double total)
        {
            if (total == 0)
                return 0;

            return (value / total) * 100;
        }

        public static bool IsPrime(this int number)
        {
            if (number <= 1)
                return false;
            if (number == 2)
                return true;
            if (number % 2 == 0)
                return false;

            var boundary = (int)Math.Floor(Math.Sqrt(number));

            for (int i = 3; i <= boundary; i += 2)
            {
                if (number % i == 0)
                    return false;
            }

            return true;
        }

        public static bool IsEven(this int number)
        {
            return number % 2 == 0;
        }

        public static bool IsOdd(this int number)
        {
            return number % 2 != 0;
        }

        public static int Absolute(this int value)
        {
            return Math.Abs(value);
        }

        public static double Absolute(this double value)
        {
            return Math.Abs(value);
        }

        public static decimal Absolute(this decimal value)
        {
            return Math.Abs(value);
        }

        public static bool ApproximatelyEquals(this float a, float b, float epsilon = 0.0001f)
        {
            return Math.Abs(a - b) < epsilon;
        }

        public static bool ApproximatelyEquals(this double a, double b, double epsilon = 0.0001)
        {
            return Math.Abs(a - b) < epsilon;
        }

        #endregion

        #region Enum Extensions

        public static string GetDescription(this Enum value)
        {
            if (value == null)
                return string.Empty;

            var field = value.GetType().GetField(value.ToString());
            if (field == null)
                return value.ToString();

            var attr = field.GetCustomAttribute<DescriptionAttribute>();
            return attr?.Description ?? value.ToString();
        }

        public static TAttribute GetAttribute<TAttribute>(this Enum value)
            where TAttribute : Attribute
        {
            if (value == null)
                return null;

            var field = value.GetType().GetField(value.ToString());
            return field?.GetCustomAttribute<TAttribute>();
        }

        #endregion

        #region Task / Async Extensions

        public static async Task<T> SafeAwait<T>(this Task<T> task, T defaultValue = default, Action<Exception> onError = null)
        {
            if (task == null)
                return defaultValue;

            try
            {
                return await task.ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                onError?.Invoke(ex);
                return defaultValue;
            }
        }

        public static async Task SafeAwait(this Task task, Action<Exception> onError = null)
        {
            if (task == null)
                return;

            try
            {
                await task.ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                onError?.Invoke(ex);
            }
        }

        public static ConfiguredTaskAwaitable<T> StayOnContext<T>(this Task<T> task)
        {
            return task.ConfigureAwait(true);
        }

        public static ConfiguredTaskAwaitable<T> DontStayOnContext<T>(this Task<T> task)
        {
            return task.ConfigureAwait(false);
        }

        public static async Task<(bool Success, T Result, Exception Error)> AsResult<T>(this Task<T> task)
        {
            try
            {
                var result = await task.ConfigureAwait(false);
                return (true, result, null);
            }
            catch (Exception ex)
            {
                return (false, default, ex);
            }
        }

        public static async Task<T> RetryWithBackoff<T>(
            this Func<Task<T>> operation,
            int maxRetries = 3,
            int initialDelayMs = 100,
            int maxDelayMs = 5000,
            Func<Exception, bool> shouldRetry = null)
        {
            if (operation == null)
                throw new ArgumentNullException(nameof(operation));

            var retries = 0;
            var delay = initialDelayMs;

            while (true)
            {
                try
                {
                    return await operation().ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    retries++;

                    if (retries > maxRetries || (shouldRetry != null && !shouldRetry(ex)))
                        throw;

                    await Task.Delay(delay).ConfigureAwait(false);
                    delay = Math.Min(delay * 2, maxDelayMs);
                }
            }
        }

        #endregion

        #region File and Stream Extensions

        public static async Task<string> ReadAllTextWithRetryAsync(this string filePath, int maxRetries = 3)
        {
            if (string.IsNullOrWhiteSpace(filePath))
                throw new ArgumentException("File path cannot be null or empty", nameof(filePath));

            for (int i = 0; i < maxRetries; i++)
            {
                try
                {
                    return await File.ReadAllTextAsync(filePath).ConfigureAwait(false);
                }
                catch when (i < maxRetries - 1)
                {
                    await Task.Delay(100 * (i + 1)).ConfigureAwait(false);
                }
            }

            // Son denemede hata fırlasın
            return await File.ReadAllTextAsync(filePath).ConfigureAwait(false);
        }

        #endregion
    }
}
