using NEDA.Core.Common;
using NEDA.Core.ExceptionHandling;
using NEDA.Core.Logging;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace NEDA.Brain.IntentRecognition.ParameterDetection;
{
    /// <summary>
    /// Parses and converts parameter values from string to various data types;
    /// with advanced pattern recognition and validation.
    /// </summary>
    public class ValueParser : IValueParser, IDisposable;
    {
        private readonly ILogger _logger;
        private readonly CultureInfo _defaultCulture;
        private readonly List<IValueValidator> _validators;
        private readonly Dictionary<Type, IValueConverter> _converters;
        private bool _disposed = false;

        // Pattern caches for performance;
        private static readonly Regex _numberRegex = new Regex(
            @"^-?\d+(?:\.\d+)?(?:[eE][+-]?\d+)?$",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        private static readonly Regex _percentageRegex = new Regex(
            @"^(\d+(?:\.\d+)?)%$",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        private static readonly Regex _rangeRegex = new Regex(
            @"^(\d+(?:\.\d+)?)\s*-\s*(\d+(?:\.\d+)?)$",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        private static readonly Regex _coordinateRegex = new Regex(
            @"^\(?\s*([+-]?\d+(?:\.\d+)?)\s*,\s*([+-]?\d+(?:\.\d+)?)\s*\)?$",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        private static readonly Regex _sizeRegex = new Regex(
            @"^(\d+(?:\.\d+)?)\s*([kmgt]?b|px|cm|mm|in|pt)$",
            RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

        private static readonly Regex _timeRegex = new Regex(
            @"^(\d{1,2}):(\d{2})(?::(\d{2}))?(?:\s*(am|pm))?$",
            RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

        private static readonly Regex _dateRegex = new Regex(
            @"^(\d{1,4})[-/.](\d{1,2})[-/.](\d{1,4})$",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        // Known boolean patterns;
        private static readonly HashSet<string> _truePatterns = new HashSet<string>(
            new[] { "true", "yes", "on", "1", "enable", "enabled", "active", "ok", "y", "t" },
            StringComparer.OrdinalIgnoreCase);

        private static readonly HashSet<string> _falsePatterns = new HashSet<string>(
            new[] { "false", "no", "off", "0", "disable", "disabled", "inactive", "cancel", "n", "f" },
            StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Initializes a new instance of ValueParser;
        /// </summary>
        public ValueParser(ILogger logger, CultureInfo culture = null)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _defaultCulture = culture ?? CultureInfo.InvariantCulture;

            _validators = new List<IValueValidator>();
            _converters = new Dictionary<Type, IValueConverter>();

            InitializeDefaultConverters();
            InitializeDefaultValidators();

            _logger.LogInformation("ValueParser initialized with culture: " + _defaultCulture.Name);
        }

        /// <summary>
        /// Initializes default type converters;
        /// </summary>
        private void InitializeDefaultConverters()
        {
            RegisterConverter<int>(new IntConverter());
            RegisterConverter<long>(new LongConverter());
            RegisterConverter<float>(new FloatConverter());
            RegisterConverter<double>(new DoubleConverter());
            RegisterConverter<decimal>(new DecimalConverter());
            RegisterConverter<bool>(new BooleanConverter());
            RegisterConverter<DateTime>(new DateTimeConverter());
            RegisterConverter<TimeSpan>(new TimeSpanConverter());
            RegisterConverter<Guid>(new GuidConverter());
            RegisterConverter<string>(new StringConverter());
            RegisterConverter<object>(new ObjectConverter());

            // Register collection converters;
            RegisterConverter<List<int>>(new ListConverter<int>());
            RegisterConverter<List<string>>(new ListConverter<string>());
            RegisterConverter<Dictionary<string, object>>(new DictionaryConverter());

            _logger.LogDebug("Default converters initialized");
        }

        /// <summary>
        /// Initializes default validators;
        /// </summary>
        private void InitializeDefaultValidators()
        {
            AddValidator(new NumberRangeValidator());
            AddValidator(new StringLengthValidator());
            AddValidator(new RegexPatternValidator());
            AddValidator(new TypeValidator());

            _logger.LogDebug("Default validators initialized");
        }

        /// <summary>
        /// Parses a string value to the specified type with validation;
        /// </summary>
        public ParseResult<T> Parse<T>(string input, ParseOptions options = null)
        {
            if (string.IsNullOrWhiteSpace(input))
            {
                return ParseResult<T>.Failure("Input cannot be null or empty",
                    ParseErrorCode.EmptyInput);
            }

            options ??= ParseOptions.Default;

            try
            {
                _logger.LogDebug($"Parsing '{input}' to type {typeof(T).Name}");

                // Apply preprocessing;
                var processedInput = PreprocessInput(input, options);

                // Get converter for type;
                var converter = GetConverter<T>();
                if (converter == null)
                {
                    return ParseResult<T>.Failure($"No converter found for type {typeof(T).Name}",
                        ParseErrorCode.NoConverterAvailable);
                }

                // Parse using converter;
                var parseResult = converter.Parse(processedInput, _defaultCulture);

                // Apply validation if successful;
                if (parseResult.IsSuccess)
                {
                    var validationResult = Validate(parseResult.Value, options);
                    if (!validationResult.IsValid)
                    {
                        return ParseResult<T>.Failure(validationResult.ErrorMessage,
                            ParseErrorCode.ValidationFailed);
                    }
                }

                _logger.LogDebug($"Parsed '{input}' to {parseResult.Value} ({typeof(T).Name})");
                return ParseResult<T>.Success(parseResult.Value);
            }
            catch (FormatException ex)
            {
                _logger.LogWarning($"Format error parsing '{input}' to {typeof(T).Name}: {ex.Message}");
                return ParseResult<T>.Failure($"Invalid format for {typeof(T).Name}: {ex.Message}",
                    ParseErrorCode.FormatError);
            }
            catch (OverflowException ex)
            {
                _logger.LogWarning($"Overflow error parsing '{input}' to {typeof(T).Name}: {ex.Message}");
                return ParseResult<T>.Failure($"Value out of range for {typeof(T).Name}",
                    ParseErrorCode.Overflow);
            }
            catch (Exception ex)
            {
                _logger.LogError($"Unexpected error parsing '{input}': {ex.Message}");
                return ParseResult<T>.Failure($"Unexpected parsing error: {ex.Message}",
                    ParseErrorCode.Unknown);
            }
        }

        /// <summary>
        /// Tries to parse a value and returns success/failure;
        /// </summary>
        public bool TryParse<T>(string input, out T value, ParseOptions options = null)
        {
            var result = Parse<T>(input, options);
            value = result.IsSuccess ? result.Value : default;
            return result.IsSuccess;
        }

        /// <summary>
        /// Parses with type inference from context;
        /// </summary>
        public object ParseWithInference(string input, Type targetType, ParseOptions options = null)
        {
            if (targetType == null)
                throw new ArgumentNullException(nameof(targetType));

            options ??= ParseOptions.Default;

            try
            {
                // Use reflection to call generic Parse method;
                var method = typeof(ValueParser).GetMethod(nameof(Parse))
                    .MakeGenericMethod(targetType);

                var result = method.Invoke(this, new object[] { input, options });
                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError($"Type inference parsing failed: {ex.Message}");
                throw new ValueParseException($"Failed to parse '{input}' as {targetType.Name}", ex);
            }
        }

        /// <summary>
        /// Parses multiple values with the same options;
        /// </summary>
        public List<ParseResult<T>> ParseMultiple<T>(IEnumerable<string> inputs, ParseOptions options = null)
        {
            if (inputs == null)
                throw new ArgumentNullException(nameof(inputs));

            options ??= ParseOptions.Default;
            var results = new List<ParseResult<T>>();

            foreach (var input in inputs)
            {
                results.Add(Parse<T>(input, options));
            }

            return results;
        }

        /// <summary>
        /// Detects the most likely type for a given input string;
        /// </summary>
        public TypeDetectionResult DetectType(string input)
        {
            if (string.IsNullOrWhiteSpace(input))
                return new TypeDetectionResult(typeof(string), 1.0);

            var candidates = new List<TypeCandidate>();

            // Check for boolean;
            if (_truePatterns.Contains(input) || _falsePatterns.Contains(input))
            {
                candidates.Add(new TypeCandidate(typeof(bool), 0.95));
            }

            // Check for integer;
            if (int.TryParse(input, NumberStyles.Integer, _defaultCulture, out _))
            {
                candidates.Add(new TypeCandidate(typeof(int), 0.9));
            }

            // Check for floating point;
            if (double.TryParse(input, NumberStyles.Float, _defaultCulture, out _))
            {
                candidates.Add(new TypeCandidate(typeof(double), 0.85));
            }

            // Check for DateTime;
            if (DateTime.TryParse(input, _defaultCulture, DateTimeStyles.None, out _))
            {
                candidates.Add(new TypeCandidate(typeof(DateTime), 0.8));
            }

            // Check for TimeSpan;
            if (TimeSpan.TryParse(input, out _))
            {
                candidates.Add(new TypeCandidate(typeof(TimeSpan), 0.75));
            }

            // Check for Guid;
            if (Guid.TryParse(input, out _))
            {
                candidates.Add(new TypeCandidate(typeof(Guid), 0.9));
            }

            // Check for percentage;
            if (_percentageRegex.IsMatch(input))
            {
                candidates.Add(new TypeCandidate(typeof(double), 0.7));
            }

            // Check for range;
            if (_rangeRegex.IsMatch(input))
            {
                candidates.Add(new TypeCandidate(typeof(Range), 0.65));
            }

            // Default to string;
            if (candidates.Count == 0)
            {
                candidates.Add(new TypeCandidate(typeof(string), 1.0));
            }

            // Return the type with highest confidence;
            var bestCandidate = candidates.OrderByDescending(c => c.Confidence).First();
            return new TypeDetectionResult(bestCandidate.Type, bestCandidate.Confidence);
        }

        /// <summary>
        /// Extracts values from complex text patterns;
        /// </summary>
        public ExtractionResult ExtractValues(string text, ExtractionPattern pattern)
        {
            if (string.IsNullOrWhiteSpace(text))
                throw new ArgumentException("Text cannot be null or empty", nameof(text));

            if (pattern == null)
                throw new ArgumentNullException(nameof(pattern));

            try
            {
                _logger.LogDebug($"Extracting values from text using pattern: {pattern.Name}");

                var matches = pattern.Regex.Matches(text);
                var results = new List<ExtractedValue>();

                foreach (Match match in matches)
                {
                    if (match.Success)
                    {
                        var extractedValue = new ExtractedValue;
                        {
                            RawText = match.Value,
                            Groups = new Dictionary<string, string>()
                        };

                        // Extract named groups;
                        foreach (var groupName in pattern.Regex.GetGroupNames())
                        {
                            if (int.TryParse(groupName, out _)) // Skip numeric groups;
                                continue;

                            var group = match.Groups[groupName];
                            if (group.Success)
                            {
                                extractedValue.Groups[groupName] = group.Value;
                            }
                        }

                        results.Add(extractedValue);
                    }
                }

                return new ExtractionResult;
                {
                    Success = true,
                    Values = results,
                    MatchCount = matches.Count;
                };
            }
            catch (Exception ex)
            {
                _logger.LogError($"Extraction failed: {ex.Message}");
                return ExtractionResult.Failure($"Extraction failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Normalizes a value to a standard format;
        /// </summary>
        public string Normalize(string input, NormalizationOptions options)
        {
            if (string.IsNullOrWhiteSpace(input))
                return input;

            var normalized = input;

            // Apply trimming;
            if (options.TrimWhitespace)
            {
                normalized = normalized.Trim();
            }

            // Apply case normalization;
            switch (options.CaseNormalization)
            {
                case CaseNormalization.Lower:
                    normalized = normalized.ToLowerInvariant();
                    break;
                case CaseNormalization.Upper:
                    normalized = normalized.ToUpperInvariant();
                    break;
                case CaseNormalization.Title:
                    normalized = CultureInfo.CurrentCulture.TextInfo.ToTitleCase(normalized);
                    break;
            }

            // Remove diacritics;
            if (options.RemoveDiacritics)
            {
                normalized = RemoveDiacritics(normalized);
            }

            // Normalize whitespace;
            if (options.NormalizeWhitespace)
            {
                normalized = Regex.Replace(normalized, @"\s+", " ");
            }

            return normalized;
        }

        /// <summary>
        /// Registers a custom type converter;
        /// </summary>
        public void RegisterConverter<T>(IValueConverter converter)
        {
            if (converter == null)
                throw new ArgumentNullException(nameof(converter));

            var type = typeof(T);
            _converters[type] = converter;
            _logger.LogDebug($"Converter registered for type: {type.Name}");
        }

        /// <summary>
        /// Adds a custom validator;
        /// </summary>
        public void AddValidator(IValueValidator validator)
        {
            if (validator == null)
                throw new ArgumentNullException(nameof(validator));

            _validators.Add(validator);
            _logger.LogDebug($"Validator added: {validator.GetType().Name}");
        }

        /// <summary>
        /// Gets the converter for a specific type;
        /// </summary>
        private IValueConverter GetConverter<T>()
        {
            var type = typeof(T);

            if (_converters.TryGetValue(type, out var converter))
            {
                return converter;
            }

            // Try to find assignable converter;
            foreach (var kvp in _converters)
            {
                if (type.IsAssignableFrom(kvp.Key))
                {
                    return kvp.Value;
                }
            }

            return null;
        }

        /// <summary>
        /// Preprocesses input based on options;
        /// </summary>
        private string PreprocessInput(string input, ParseOptions options)
        {
            if (string.IsNullOrWhiteSpace(input))
                return input;

            var processed = input;

            // Apply normalization;
            if (options.NormalizationOptions != null)
            {
                processed = Normalize(processed, options.NormalizationOptions);
            }

            // Remove specified characters;
            if (options.CharactersToRemove != null)
            {
                foreach (var ch in options.CharactersToRemove)
                {
                    processed = processed.Replace(ch.ToString(), "");
                }
            }

            return processed;
        }

        /// <summary>
        /// Validates a parsed value;
        /// </summary>
        private ValidationResult Validate<T>(T value, ParseOptions options)
        {
            var validationErrors = new List<string>();

            foreach (var validator in _validators)
            {
                if (validator.CanValidate(typeof(T)))
                {
                    var result = validator.Validate(value, options);
                    if (!result.IsValid)
                    {
                        validationErrors.Add(result.ErrorMessage);
                    }
                }
            }

            // Apply custom validations from options;
            if (options.CustomValidations != null)
            {
                foreach (var validation in options.CustomValidations)
                {
                    if (!validation(value))
                    {
                        validationErrors.Add("Custom validation failed");
                    }
                }
            }

            if (validationErrors.Count > 0)
            {
                return ValidationResult.Failure(string.Join("; ", validationErrors));
            }

            return ValidationResult.Success();
        }

        /// <summary>
        /// Removes diacritics from text;
        /// </summary>
        private static string RemoveDiacritics(string text)
        {
            var normalizedString = text.Normalize(System.Text.NormalizationForm.FormD);
            var stringBuilder = new System.Text.StringBuilder();

            foreach (var c in normalizedString)
            {
                var unicodeCategory = CharUnicodeInfo.GetUnicodeCategory(c);
                if (unicodeCategory != UnicodeCategory.NonSpacingMark)
                {
                    stringBuilder.Append(c);
                }
            }

            return stringBuilder.ToString().Normalize(System.Text.NormalizationForm.FormC);
        }

        /// <summary>
        /// Clears all registered converters and validators;
        /// </summary>
        public void Clear()
        {
            _converters.Clear();
            _validators.Clear();
            _logger.LogInformation("All converters and validators cleared");
        }

        #region IDisposable Implementation;

        protected virtual void Dispose(bool disposing)
        {
            if (!_disposed)
            {
                if (disposing)
                {
                    // Cleanup managed resources;
                    _converters.Clear();
                    _validators.Clear();
                    _logger.LogDebug("ValueParser disposed");
                }

                _disposed = true;
            }
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        ~ValueParser()
        {
            Dispose(false);
        }

        #endregion;
    }

    #region Supporting Interfaces and Classes;

    public interface IValueParser;
    {
        ParseResult<T> Parse<T>(string input, ParseOptions options);
        bool TryParse<T>(string input, out T value, ParseOptions options);
        TypeDetectionResult DetectType(string input);
        ExtractionResult ExtractValues(string text, ExtractionPattern pattern);
        void RegisterConverter<T>(IValueConverter converter);
        void AddValidator(IValueValidator validator);
    }

    public interface IValueConverter;
    {
        ConversionResult Parse(string input, CultureInfo culture);
        bool CanConvert(Type type);
    }

    public interface IValueValidator;
    {
        ValidationResult Validate(object value, ParseOptions options);
        bool CanValidate(Type type);
    }

    /// <summary>
    /// Options for parsing operations;
    /// </summary>
    public class ParseOptions;
    {
        public static ParseOptions Default => new ParseOptions();

        public bool AllowNull { get; set; } = true;
        public bool AllowEmpty { get; set; } = true;
        public NormalizationOptions NormalizationOptions { get; set; }
        public HashSet<char> CharactersToRemove { get; set; }
        public List<Func<object, bool>> CustomValidations { get; set; }
        public Dictionary<string, object> Context { get; set; }

        public ParseOptions()
        {
            NormalizationOptions = new NormalizationOptions();
            CharactersToRemove = new HashSet<char>();
            CustomValidations = new List<Func<object, bool>>();
            Context = new Dictionary<string, object>();
        }
    }

    /// <summary>
    /// Options for text normalization;
    /// </summary>
    public class NormalizationOptions;
    {
        public bool TrimWhitespace { get; set; } = true;
        public CaseNormalization CaseNormalization { get; set; } = CaseNormalization.None;
        public bool RemoveDiacritics { get; set; } = false;
        public bool NormalizeWhitespace { get; set; } = true;
    }

    public enum CaseNormalization;
    {
        None,
        Lower,
        Upper,
        Title;
    }

    /// <summary>
    /// Result of a parsing operation;
    /// </summary>
    public class ParseResult<T>
    {
        public bool IsSuccess { get; }
        public T Value { get; }
        public string ErrorMessage { get; }
        public ParseErrorCode ErrorCode { get; }

        private ParseResult(T value, bool success, string errorMessage, ParseErrorCode errorCode)
        {
            Value = value;
            IsSuccess = success;
            ErrorMessage = errorMessage;
            ErrorCode = errorCode;
        }

        public static ParseResult<T> Success(T value)
        {
            return new ParseResult<T>(value, true, null, ParseErrorCode.None);
        }

        public static ParseResult<T> Failure(string errorMessage, ParseErrorCode errorCode)
        {
            return new ParseResult<T>(default, false, errorMessage, errorCode);
        }

        public override string ToString()
        {
            return IsSuccess;
                ? $"Success: {Value}"
                : $"Failure: {ErrorMessage} ({ErrorCode})";
        }
    }

    /// <summary>
    /// Result of type detection;
    /// </summary>
    public class TypeDetectionResult;
    {
        public Type DetectedType { get; }
        public double Confidence { get; }

        public TypeDetectionResult(Type detectedType, double confidence)
        {
            DetectedType = detectedType ?? throw new ArgumentNullException(nameof(detectedType));
            Confidence = confidence;
        }
    }

    /// <summary>
    /// Result of value extraction;
    /// </summary>
    public class ExtractionResult;
    {
        public bool Success { get; set; }
        public string ErrorMessage { get; set; }
        public List<ExtractedValue> Values { get; set; }
        public int MatchCount { get; set; }

        public static ExtractionResult Failure(string errorMessage)
        {
            return new ExtractionResult;
            {
                Success = false,
                ErrorMessage = errorMessage,
                Values = new List<ExtractedValue>(),
                MatchCount = 0;
            };
        }
    }

    /// <summary>
    /// Pattern for value extraction;
    /// </summary>
    public class ExtractionPattern;
    {
        public string Name { get; set; }
        public Regex Regex { get; set; }
        public string Description { get; set; }
        public Dictionary<string, Type> ExpectedGroups { get; set; }

        public ExtractionPattern(string name, string pattern)
        {
            Name = name;
            Regex = new Regex(pattern, RegexOptions.Compiled | RegexOptions.CultureInvariant);
            ExpectedGroups = new Dictionary<string, Type>();
        }
    }

    /// <summary>
    /// Extracted value from text;
    /// </summary>
    public class ExtractedValue;
    {
        public string RawText { get; set; }
        public Dictionary<string, string> Groups { get; set; }
    }

    /// <summary>
    /// Result of conversion operation;
    /// </summary>
    public class ConversionResult;
    {
        public bool IsSuccess { get; }
        public object Value { get; }
        public string ErrorMessage { get; }

        private ConversionResult(object value, bool success, string errorMessage)
        {
            Value = value;
            IsSuccess = success;
            ErrorMessage = errorMessage;
        }

        public static ConversionResult Success(object value)
        {
            return new ConversionResult(value, true, null);
        }

        public static ConversionResult Failure(string errorMessage)
        {
            return new ConversionResult(null, false, errorMessage);
        }
    }

    /// <summary>
    /// Result of validation;
    /// </summary>
    public class ValidationResult;
    {
        public bool IsValid { get; }
        public string ErrorMessage { get; }

        private ValidationResult(bool isValid, string errorMessage)
        {
            IsValid = isValid;
            ErrorMessage = errorMessage;
        }

        public static ValidationResult Success()
        {
            return new ValidationResult(true, null);
        }

        public static ValidationResult Failure(string errorMessage)
        {
            return new ValidationResult(false, errorMessage);
        }
    }

    /// <summary>
    /// Type candidate for detection;
    /// </summary>
    internal class TypeCandidate;
    {
        public Type Type { get; }
        public double Confidence { get; }

        public TypeCandidate(Type type, double confidence)
        {
            Type = type;
            Confidence = confidence;
        }
    }

    /// <summary>
    /// Range structure;
    /// </summary>
    public struct Range;
    {
        public double Min { get; }
        public double Max { get; }

        public Range(double min, double max)
        {
            Min = min;
            Max = max;
        }

        public override string ToString() => $"{Min} - {Max}";
    }

    /// <summary>
    /// Error codes for parsing operations;
    /// </summary>
    public enum ParseErrorCode;
    {
        None = 0,
        EmptyInput,
        FormatError,
        Overflow,
        ValidationFailed,
        NoConverterAvailable,
        Unknown;
    }

    /// <summary>
    /// Custom exception for parsing errors;
    /// </summary>
    public class ValueParseException : Exception
    {
        public ParseErrorCode ErrorCode { get; }

        public ValueParseException(string message) : base(message)
        {
            ErrorCode = ParseErrorCode.Unknown;
        }

        public ValueParseException(string message, Exception innerException)
            : base(message, innerException)
        {
            ErrorCode = ParseErrorCode.Unknown;
        }

        public ValueParseException(string message, ParseErrorCode errorCode)
            : base(message)
        {
            ErrorCode = errorCode;
        }
    }

    #endregion;

    #region Default Converter Implementations;

    internal class IntConverter : IValueConverter;
    {
        public ConversionResult Parse(string input, CultureInfo culture)
        {
            if (int.TryParse(input, NumberStyles.Integer, culture, out var result))
                return ConversionResult.Success(result);

            return ConversionResult.Failure($"Cannot convert '{input}' to integer");
        }

        public bool CanConvert(Type type) => type == typeof(int);
    }

    internal class DoubleConverter : IValueConverter;
    {
        public ConversionResult Parse(string input, CultureInfo culture)
        {
            if (double.TryParse(input, NumberStyles.Float, culture, out var result))
                return ConversionResult.Success(result);

            return ConversionResult.Failure($"Cannot convert '{input}' to double");
        }

        public bool CanConvert(Type type) => type == typeof(double);
    }

    internal class BooleanConverter : IValueConverter;
    {
        private static readonly HashSet<string> _trueValues = new HashSet<string>
        {
            "true", "yes", "on", "1", "enable", "enabled", "active", "ok", "y", "t"
        };

        private static readonly HashSet<string> _falseValues = new HashSet<string>
        {
            "false", "no", "off", "0", "disable", "disabled", "inactive", "cancel", "n", "f"
        };

        public ConversionResult Parse(string input, CultureInfo culture)
        {
            var normalized = input.Trim().ToLowerInvariant();

            if (_trueValues.Contains(normalized))
                return ConversionResult.Success(true);

            if (_falseValues.Contains(normalized))
                return ConversionResult.Success(false);

            return ConversionResult.Failure($"Cannot convert '{input}' to boolean");
        }

        public bool CanConvert(Type type) => type == typeof(bool);
    }

    internal class DateTimeConverter : IValueConverter;
    {
        public ConversionResult Parse(string input, CultureInfo culture)
        {
            if (DateTime.TryParse(input, culture, DateTimeStyles.None, out var result))
                return ConversionResult.Success(result);

            return ConversionResult.Failure($"Cannot convert '{input}' to DateTime");
        }

        public bool CanConvert(Type type) => type == typeof(DateTime);
    }

    internal class StringConverter : IValueConverter;
    {
        public ConversionResult Parse(string input, CultureInfo culture)
        {
            return ConversionResult.Success(input);
        }

        public bool CanConvert(Type type) => type == typeof(string);
    }

    internal class ListConverter<T> : IValueConverter;
    {
        public ConversionResult Parse(string input, CultureInfo culture)
        {
            try
            {
                var items = input.Split(new[] { ',', ';', '|' }, StringSplitOptions.RemoveEmptyEntries)
                    .Select(s => s.Trim())
                    .ToList();

                // This is a simplified implementation;
                // In reality, you'd parse each item to type T;
                return ConversionResult.Success(items);
            }
            catch (Exception ex)
            {
                return ConversionResult.Failure($"Cannot convert '{input}' to List<{typeof(T).Name}>: {ex.Message}");
            }
        }

        public bool CanConvert(Type type) =>
            type.IsGenericType && type.GetGenericTypeDefinition() == typeof(List<>);
    }

    // Additional converters can be added here...

    #endregion;

    #region Default Validator Implementations;

    internal class NumberRangeValidator : IValueValidator;
    {
        public ValidationResult Validate(object value, ParseOptions options)
        {
            if (value is IComparable comparable)
            {
                // Example range validation;
                // In practice, you'd get range constraints from options.Context;
                return ValidationResult.Success();
            }

            return ValidationResult.Failure("Value is not comparable");
        }

        public bool CanValidate(Type type)
        {
            return type.IsPrimitive || type == typeof(decimal);
        }
    }

    internal class StringLengthValidator : IValueValidator;
    {
        public ValidationResult Validate(object value, ParseOptions options)
        {
            if (value is string str)
            {
                if (str.Length > 1000) // Example limit;
                    return ValidationResult.Failure("String length exceeds maximum");

                return ValidationResult.Success();
            }

            return ValidationResult.Success(); // Not applicable to non-strings;
        }

        public bool CanValidate(Type type) => type == typeof(string);
    }

    #endregion;
}
