using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;
using NEDA.Core.Common;

namespace NEDA.Brain.NLP_Engine.EntityRecognition;
{
    /// <summary>
    /// Named Entity - Represents a recognized named entity in text with semantic information,
    /// confidence scoring, and contextual metadata;
    /// </summary>
    public class NamedEntity : IEquatable<NamedEntity>, IComparable<NamedEntity>
    {
        /// <summary>
        /// Unique identifier for the entity;
        /// </summary>
        public Guid Id { get; private set; }

        /// <summary>
        /// The text value of the entity;
        /// </summary>
        public string Text { get; private set; }

        /// <summary>
        /// The normalized/standardized form of the entity;
        /// </summary>
        public string NormalizedText { get; private set; }

        /// <summary>
        /// Type/category of the entity;
        /// </summary>
        public EntityType Type { get; private set; }

        /// <summary>
        /// Subtype for more specific categorization;
        /// </summary>
        public string Subtype { get; private set; }

        /// <summary>
        /// Starting position in the source text;
        /// </summary>
        public int StartIndex { get; private set; }

        /// <summary>
        /// Ending position in the source text;
        /// </summary>
        public int EndIndex { get; private set; }

        /// <summary>
        /// Confidence score (0.0 to 1.0)
        /// </summary>
        public double Confidence { get; private set; }

        /// <summary>
        /// Semantic meaning/definition of the entity;
        /// </summary>
        public string Meaning { get; private set; }

        /// <summary>
        /// Knowledge base URI or identifier;
        /// </summary>
        public string KnowledgeBaseUri { get; private set; }

        /// <summary>
        /// Wikipedia or external reference URL;
        /// </summary>
        public string ReferenceUrl { get; private set; }

        /// <summary>
        /// Additional metadata and properties;
        /// </summary>
        public Dictionary<string, object> Metadata { get; private set; }

        /// <summary>
        /// Temporal information if applicable;
        /// </summary>
        public TemporalInfo TemporalInfo { get; private set; }

        /// <summary>
        /// Geographical information if applicable;
        /// </summary>
        public GeoInfo GeoInfo { get; private set; }

        /// <summary>
        /// Numeric information if applicable;
        /// </summary>
        public NumericInfo NumericInfo { get; private set; }

        /// <summary>
        /// Relationships to other entities;
        /// </summary>
        public List<EntityRelation> Relations { get; private set; }

        /// <summary>
        /// Disambiguation information;
        /// </summary>
        public DisambiguationInfo Disambiguation { get; private set; }

        /// <summary>
        /// Entity sentiment/polarity;
        /// </summary>
        public EntitySentiment Sentiment { get; private set; }

        /// <summary>
        /// Source document/location information;
        /// </summary>
        public EntitySource Source { get; private set; }

        /// <summary>
        /// Processing/recognition timestamp;
        /// </summary>
        public DateTime RecognizedAt { get; private set; }

        /// <summary>
        /// Processing pipeline identifier;
        /// </summary>
        public string ProcessorId { get; private set; }

        /// <summary>
        /// Language of the entity text;
        /// </summary>
        public string Language { get; private set; }

        /// <summary>
        /// Gets the length of the entity text;
        /// </summary>
        [JsonIgnore]
        public int Length => EndIndex - StartIndex;

        /// <summary>
        /// Gets whether the entity has high confidence;
        /// </summary>
        [JsonIgnore]
        public bool IsHighConfidence => Confidence >= 0.8;

        /// <summary>
        /// Gets whether the entity is ambiguous;
        /// </summary>
        [JsonIgnore]
        public bool IsAmbiguous => Disambiguation?.AmbiguityScore > 0.3;

        /// <summary>
        /// Gets whether the entity has geographical information;
        /// </summary>
        [JsonIgnore]
        public bool HasGeoInfo => GeoInfo != null;

        /// <summary>
        /// Gets whether the entity has temporal information;
        /// </summary>
        [JsonIgnore]
        public bool HasTemporalInfo => TemporalInfo != null;

        /// <summary>
        /// Gets whether the entity has numeric information;
        /// </summary>
        [JsonIgnore]
        public bool HasNumericInfo => NumericInfo != null;

        /// <summary>
        /// Gets a value indicating if this is a core entity (high importance)
        /// </summary>
        [JsonIgnore]
        public bool IsCoreEntity => Type.IsCoreEntityType() && Confidence >= 0.7;

        /// <summary>
        /// Private constructor for factory methods;
        /// </summary>
        private NamedEntity()
        {
            Id = Guid.NewGuid();
            Metadata = new Dictionary<string, object>();
            Relations = new List<EntityRelation>();
            RecognizedAt = DateTime.UtcNow;
        }

        /// <summary>
        /// Creates a new NamedEntity with the specified text and type;
        /// </summary>
        public NamedEntity(string text, EntityType type, int startIndex, int endIndex, double confidence = 1.0)
            : this()
        {
            if (string.IsNullOrWhiteSpace(text))
                throw new ArgumentException("Entity text cannot be null or empty", nameof(text));

            if (startIndex < 0)
                throw new ArgumentOutOfRangeException(nameof(startIndex), "Start index cannot be negative");

            if (endIndex <= startIndex)
                throw new ArgumentOutOfRangeException(nameof(endIndex), "End index must be greater than start index");

            if (confidence < 0.0 || confidence > 1.0)
                throw new ArgumentOutOfRangeException(nameof(confidence), "Confidence must be between 0.0 and 1.0");

            Text = text.Trim();
            NormalizedText = NormalizeText(text);
            Type = type;
            StartIndex = startIndex;
            EndIndex = endIndex;
            Confidence = confidence;
            Language = DetectLanguage(text);
        }

        /// <summary>
        /// Creates a deep copy of the entity;
        /// </summary>
        public NamedEntity Clone()
        {
            return new NamedEntity;
            {
                Id = this.Id,
                Text = this.Text,
                NormalizedText = this.NormalizedText,
                Type = this.Type,
                Subtype = this.Subtype,
                StartIndex = this.StartIndex,
                EndIndex = this.EndIndex,
                Confidence = this.Confidence,
                Meaning = this.Meaning,
                KnowledgeBaseUri = this.KnowledgeBaseUri,
                ReferenceUrl = this.ReferenceUrl,
                Metadata = new Dictionary<string, object>(this.Metadata),
                TemporalInfo = this.TemporalInfo?.Clone(),
                GeoInfo = this.GeoInfo?.Clone(),
                NumericInfo = this.NumericInfo?.Clone(),
                Relations = this.Relations.Select(r => r.Clone()).ToList(),
                Disambiguation = this.Disambiguation?.Clone(),
                Sentiment = this.Sentiment?.Clone(),
                Source = this.Source?.Clone(),
                RecognizedAt = this.RecognizedAt,
                ProcessorId = this.ProcessorId,
                Language = this.Language;
            };
        }

        /// <summary>
        /// Updates the entity with new text and position information;
        /// </summary>
        public void UpdatePosition(string newText, int newStartIndex, int newEndIndex)
        {
            if (string.IsNullOrWhiteSpace(newText))
                throw new ArgumentException("Text cannot be null or empty", nameof(newText));

            if (newStartIndex < 0)
                throw new ArgumentOutOfRangeException(nameof(newStartIndex), "Start index cannot be negative");

            if (newEndIndex <= newStartIndex)
                throw new ArgumentOutOfRangeException(nameof(newEndIndex), "End index must be greater than start index");

            Text = newText.Trim();
            NormalizedText = NormalizeText(newText);
            StartIndex = newStartIndex;
            EndIndex = newEndIndex;
        }

        /// <summary>
        /// Sets the entity subtype;
        /// </summary>
        public NamedEntity WithSubtype(string subtype)
        {
            Subtype = subtype;
            return this;
        }

        /// <summary>
        /// Sets the confidence score;
        /// </summary>
        public NamedEntity WithConfidence(double confidence)
        {
            if (confidence < 0.0 || confidence > 1.0)
                throw new ArgumentOutOfRangeException(nameof(confidence), "Confidence must be between 0.0 and 1.0");

            Confidence = confidence;
            return this;
        }

        /// <summary>
        /// Sets the semantic meaning;
        /// </summary>
        public NamedEntity WithMeaning(string meaning)
        {
            Meaning = meaning;
            return this;
        }

        /// <summary>
        /// Sets the knowledge base URI;
        /// </summary>
        public NamedEntity WithKnowledgeBaseUri(string uri)
        {
            KnowledgeBaseUri = uri;
            return this;
        }

        /// <summary>
        /// Sets the reference URL;
        /// </summary>
        public NamedEntity WithReferenceUrl(string url)
        {
            ReferenceUrl = url;
            return this;
        }

        /// <summary>
        /// Adds metadata to the entity;
        /// </summary>
        public NamedEntity WithMetadata(string key, object value)
        {
            if (string.IsNullOrWhiteSpace(key))
                throw new ArgumentException("Metadata key cannot be null or empty", nameof(key));

            Metadata[key] = value;
            return this;
        }

        /// <summary>
        /// Sets temporal information;
        /// </summary>
        public NamedEntity WithTemporalInfo(TemporalInfo temporalInfo)
        {
            TemporalInfo = temporalInfo;
            return this;
        }

        /// <summary>
        /// Sets geographical information;
        /// </summary>
        public NamedEntity WithGeoInfo(GeoInfo geoInfo)
        {
            GeoInfo = geoInfo;
            return this;
        }

        /// <summary>
        /// Sets numeric information;
        /// </summary>
        public NamedEntity WithNumericInfo(NumericInfo numericInfo)
        {
            NumericInfo = numericInfo;
            return this;
        }

        /// <summary>
        /// Adds a relation to another entity;
        /// </summary>
        public NamedEntity WithRelation(EntityRelation relation)
        {
            if (relation == null)
                throw new ArgumentNullException(nameof(relation));

            Relations.Add(relation);
            return this;
        }

        /// <summary>
        /// Sets disambiguation information;
        /// </summary>
        public NamedEntity WithDisambiguation(DisambiguationInfo disambiguation)
        {
            Disambiguation = disambiguation;
            return this;
        }

        /// <summary>
        /// Sets entity sentiment;
        /// </summary>
        public NamedEntity WithSentiment(EntitySentiment sentiment)
        {
            Sentiment = sentiment;
            return this;
        }

        /// <summary>
        /// Sets source information;
        /// </summary>
        public NamedEntity WithSource(EntitySource source)
        {
            Source = source;
            return this;
        }

        /// <summary>
        /// Sets the processor identifier;
        /// </summary>
        public NamedEntity WithProcessorId(string processorId)
        {
            ProcessorId = processorId;
            return this;
        }

        /// <summary>
        /// Sets the language;
        /// </summary>
        public NamedEntity WithLanguage(string language)
        {
            if (string.IsNullOrWhiteSpace(language))
                throw new ArgumentException("Language cannot be null or empty", nameof(language));

            Language = language;
            return this;
        }

        /// <summary>
        /// Merges this entity with another entity (for co-referencing)
        /// </summary>
        public NamedEntity MergeWith(NamedEntity other, MergeStrategy strategy = MergeStrategy.Intelligent)
        {
            if (other == null)
                throw new ArgumentNullException(nameof(other));

            if (!CanMergeWith(other))
                throw new InvalidOperationException("Entities cannot be merged due to incompatible types or positions");

            var merged = this.Clone();

            // Apply merge strategy;
            switch (strategy)
            {
                case MergeStrategy.PreferSource:
                    // Keep source values;
                    break;

                case MergeStrategy.PreferTarget:
                    merged.Text = other.Text;
                    merged.NormalizedText = other.NormalizedText;
                    merged.StartIndex = Math.Min(this.StartIndex, other.StartIndex);
                    merged.EndIndex = Math.Max(this.EndIndex, other.EndIndex);
                    merged.Confidence = Math.Max(this.Confidence, other.Confidence);
                    break;

                case MergeStrategy.Intelligent:
                    merged.Confidence = (this.Confidence + other.Confidence) / 2.0;

                    // Combine metadata;
                    foreach (var kvp in other.Metadata)
                    {
                        if (!merged.Metadata.ContainsKey(kvp.Key))
                        {
                            merged.Metadata[kvp.Key] = kvp.Value;
                        }
                    }

                    // Combine relations;
                    merged.Relations.AddRange(other.Relations.Where(r =>
                        !merged.Relations.Any(mr => mr.TargetEntityId == r.TargetEntityId)));

                    // Update position to cover both entities;
                    merged.StartIndex = Math.Min(this.StartIndex, other.StartIndex);
                    merged.EndIndex = Math.Max(this.EndIndex, other.EndIndex);
                    break;

                case MergeStrategy.WeightedAverage:
                    var weightThis = this.Confidence;
                    var weightOther = other.Confidence;
                    var totalWeight = weightThis + weightOther;

                    merged.Confidence = (weightThis * this.Confidence + weightOther * other.Confidence) / totalWeight;
                    break;
            }

            // Update recognition time;
            merged.RecognizedAt = DateTime.UtcNow;

            return merged;
        }

        /// <summary>
        /// Checks if this entity can be merged with another entity;
        /// </summary>
        public bool CanMergeWith(NamedEntity other)
        {
            if (other == null)
                return false;

            // Check if entities refer to the same thing;
            if (this.Type != other.Type)
                return false;

            // Check if text is similar;
            if (!IsTextSimilar(this.NormalizedText, other.NormalizedText))
                return false;

            // Check if positions overlap or are adjacent;
            if (!PositionsOverlapOrAdjacent(this, other))
                return false;

            return true;
        }

        /// <summary>
        /// Gets the entity score based on multiple factors;
        /// </summary>
        public double CalculateEntityScore()
        {
            double score = Confidence;

            // Type importance bonus;
            score += Type.GetImportanceWeight() * 0.2;

            // Length bonus (longer entities are usually more specific)
            if (Length > 3)
                score += Math.Min(Length / 50.0, 0.1);

            // Metadata bonus;
            if (Metadata.Count > 0)
                score += Math.Min(Metadata.Count * 0.02, 0.1);

            // Relations bonus;
            if (Relations.Count > 0)
                score += Math.Min(Relations.Count * 0.03, 0.15);

            // Disambiguation penalty;
            if (IsAmbiguous)
                score -= Disambiguation.AmbiguityScore * 0.2;

            return Math.Max(0.0, Math.Min(score, 1.0));
        }

        /// <summary>
        /// Extracts key information for indexing/search;
        /// </summary>
        public EntityIndexData ToIndexData()
        {
            return new EntityIndexData;
            {
                EntityId = Id,
                Text = Text,
                NormalizedText = NormalizedText,
                Type = Type,
                Subtype = Subtype,
                Confidence = Confidence,
                Language = Language,
                HasGeoInfo = HasGeoInfo,
                HasTemporalInfo = HasTemporalInfo,
                HasNumericInfo = HasNumericInfo,
                IsCoreEntity = IsCoreEntity,
                Score = CalculateEntityScore(),
                Keywords = ExtractKeywords(),
                Timestamp = RecognizedAt;
            };
        }

        /// <summary>
        /// Creates a simplified representation for serialization;
        /// </summary>
        public EntitySummary ToSummary()
        {
            return new EntitySummary;
            {
                Id = Id,
                Text = Text,
                Type = Type.ToString(),
                Subtype = Subtype,
                Confidence = Confidence,
                StartIndex = StartIndex,
                EndIndex = EndIndex,
                IsCoreEntity = IsCoreEntity,
                HasGeoInfo = HasGeoInfo,
                HasTemporalInfo = HasTemporalInfo,
                RelationsCount = Relations.Count,
                RecognizedAt = RecognizedAt;
            };
        }

        /// <summary>
        /// Validates the entity for consistency;
        /// </summary>
        public ValidationResult Validate()
        {
            var errors = new List<string>();

            if (string.IsNullOrWhiteSpace(Text))
                errors.Add("Entity text is required");

            if (StartIndex < 0)
                errors.Add("Start index cannot be negative");

            if (EndIndex <= StartIndex)
                errors.Add("End index must be greater than start index");

            if (Confidence < 0.0 || Confidence > 1.0)
                errors.Add($"Confidence must be between 0.0 and 1.0, got {Confidence}");

            if (!Enum.IsDefined(typeof(EntityType), Type))
                errors.Add($"Invalid entity type: {Type}");

            if (Text.Length != (EndIndex - StartIndex))
                errors.Add($"Text length ({Text.Length}) doesn't match position range ({EndIndex - StartIndex})");

            return new ValidationResult;
            {
                IsValid = errors.Count == 0,
                Errors = errors;
            };
        }

        /// <summary>
        /// Compares this entity with another for equality;
        /// </summary>
        public bool Equals(NamedEntity other)
        {
            if (other is null) return false;
            if (ReferenceEquals(this, other)) return true;

            return Id.Equals(other.Id) &&
                   string.Equals(NormalizedText, other.NormalizedText, StringComparison.OrdinalIgnoreCase) &&
                   Type == other.Type &&
                   StartIndex == other.StartIndex &&
                   EndIndex == other.EndIndex;
        }

        /// <summary>
        /// Compares this entity with another for sorting;
        /// </summary>
        public int CompareTo(NamedEntity other)
        {
            if (other is null) return 1;

            // First by start index;
            var startComparison = StartIndex.CompareTo(other.StartIndex);
            if (startComparison != 0) return startComparison;

            // Then by end index (longer entities first)
            var endComparison = other.EndIndex.CompareTo(EndIndex);
            if (endComparison != 0) return endComparison;

            // Then by confidence;
            var confidenceComparison = other.Confidence.CompareTo(Confidence);
            if (confidenceComparison != 0) return confidenceComparison;

            // Finally by text;
            return string.Compare(Text, other.Text, StringComparison.Ordinal);
        }

        public override bool Equals(object obj) => Equals(obj as NamedEntity);

        public override int GetHashCode()
        {
            unchecked;
            {
                int hash = 17;
                hash = hash * 23 + Id.GetHashCode();
                hash = hash * 23 + (NormalizedText?.GetHashCode(StringComparison.OrdinalIgnoreCase) ?? 0);
                hash = hash * 23 + Type.GetHashCode();
                hash = hash * 23 + StartIndex.GetHashCode();
                hash = hash * 23 + EndIndex.GetHashCode();
                return hash;
            }
        }

        public override string ToString()
        {
            return $"[{Type}:{Text}]({StartIndex}-{EndIndex}, Confidence: {Confidence:P0})";
        }

        #region Static Factory Methods;

        /// <summary>
        /// Creates a person entity;
        /// </summary>
        public static NamedEntity CreatePerson(string name, int startIndex, int endIndex, double confidence = 1.0)
        {
            return new NamedEntity(name, EntityType.Person, startIndex, endIndex, confidence)
                .WithSubtype("Individual");
        }

        /// <summary>
        /// Creates an organization entity;
        /// </summary>
        public static NamedEntity CreateOrganization(string name, int startIndex, int endIndex, double confidence = 1.0)
        {
            return new NamedEntity(name, EntityType.Organization, startIndex, endIndex, confidence)
                .WithSubtype("Company");
        }

        /// <summary>
        /// Creates a location entity;
        /// </summary>
        public static NamedEntity CreateLocation(string name, int startIndex, int endIndex, double confidence = 1.0)
        {
            return new NamedEntity(name, EntityType.Location, startIndex, endIndex, confidence)
                .WithSubtype("Geographic");
        }

        /// <summary>
        /// Creates a date entity;
        /// </summary>
        public static NamedEntity CreateDate(string text, int startIndex, int endIndex, DateTime date, double confidence = 1.0)
        {
            return new NamedEntity(text, EntityType.Date, startIndex, endIndex, confidence)
                .WithSubtype("CalendarDate")
                .WithTemporalInfo(new TemporalInfo;
                {
                    DateTimeValue = date,
                    TemporalType = TemporalType.Date;
                });
        }

        /// <summary>
        /// Creates a monetary amount entity;
        /// </summary>
        public static NamedEntity CreateMoney(string text, int startIndex, int endIndex, decimal amount, string currency, double confidence = 1.0)
        {
            return new NamedEntity(text, EntityType.Money, startIndex, endIndex, confidence)
                .WithSubtype("CurrencyAmount")
                .WithNumericInfo(new NumericInfo;
                {
                    NumericValue = (double)amount,
                    Unit = currency,
                    NumericType = NumericType.Currency;
                });
        }

        /// <summary>
        /// Creates a percentage entity;
        /// </summary>
        public static NamedEntity CreatePercentage(string text, int startIndex, int endIndex, double value, double confidence = 1.0)
        {
            return new NamedEntity(text, EntityType.Percent, startIndex, endIndex, confidence)
                .WithSubtype("Ratio")
                .WithNumericInfo(new NumericInfo;
                {
                    NumericValue = value,
                    Unit = "%",
                    NumericType = NumericType.Percentage;
                });
        }

        /// <summary>
        /// Creates a time entity;
        /// </summary>
        public static NamedEntity CreateTime(string text, int startIndex, int endIndex, TimeSpan time, double confidence = 1.0)
        {
            return new NamedEntity(text, EntityType.Time, startIndex, endIndex, confidence)
                .WithSubtype("ClockTime")
                .WithTemporalInfo(new TemporalInfo;
                {
                    TimeSpanValue = time,
                    TemporalType = TemporalType.Time;
                });
        }

        /// <summary>
        /// Creates a product entity;
        /// </summary>
        public static NamedEntity CreateProduct(string name, int startIndex, int endIndex, double confidence = 1.0)
        {
            return new NamedEntity(name, EntityType.Product, startIndex, endIndex, confidence)
                .WithSubtype("CommercialProduct");
        }

        /// <summary>
        /// Creates an event entity;
        /// </summary>
        public static NamedEntity CreateEvent(string name, int startIndex, int endIndex, double confidence = 1.0)
        {
            return new NamedEntity(name, EntityType.Event, startIndex, endIndex, confidence)
                .WithSubtype("Occurrence");
        }

        #endregion;

        #region Private Helper Methods;

        private string NormalizeText(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return text;

            // Convert to lowercase, trim, and normalize whitespace;
            var normalized = text.Trim()
                .ToLowerInvariant()
                .Replace("\r\n", " ")
                .Replace("\n", " ")
                .Replace("\t", " ");

            // Remove extra whitespace;
            while (normalized.Contains("  "))
                normalized = normalized.Replace("  ", " ");

            // Apply language-specific normalization;
            if (!string.IsNullOrEmpty(Language))
            {
                normalized = ApplyLanguageNormalization(normalized, Language);
            }

            return normalized;
        }

        private string ApplyLanguageNormalization(string text, string language)
        {
            // Language-specific normalization rules;
            return language.ToLowerInvariant() switch;
            {
                "tr" => text.Replace("ı", "i").Replace("İ", "i"),
                "de" => text.Replace("ß", "ss"),
                _ => text;
            };
        }

        private string DetectLanguage(string text)
        {
            // Simple language detection based on common patterns;
            // In production, use a proper language detection library;
            if (string.IsNullOrWhiteSpace(text) || text.Length < 3)
                return "en";

            // Check for Turkish specific characters;
            if (text.Contains("ğ") || text.Contains("ş") || text.Contains("ı") || text.Contains("İ"))
                return "tr";

            // Check for German specific characters;
            if (text.Contains("ß") || text.Contains("ä") || text.Contains("ö") || text.Contains("ü"))
                return "de";

            // Default to English;
            return "en";
        }

        private bool IsTextSimilar(string text1, string text2)
        {
            if (string.IsNullOrEmpty(text1) || string.IsNullOrEmpty(text2))
                return false;

            // Simple similarity check - in production use proper string similarity algorithms;
            var normalized1 = text1.ToLowerInvariant().Trim();
            var normalized2 = text2.ToLowerInvariant().Trim();

            if (normalized1 == normalized2)
                return true;

            // Check for containment;
            if (normalized1.Contains(normalized2) || normalized2.Contains(normalized1))
                return true;

            // Calculate Levenshtein distance (simplified)
            var distance = CalculateLevenshteinDistance(normalized1, normalized2);
            var maxLength = Math.Max(normalized1.Length, normalized2.Length);
            var similarity = 1.0 - (distance / (double)maxLength);

            return similarity >= 0.7; // 70% similarity threshold;
        }

        private int CalculateLevenshteinDistance(string a, string b)
        {
            if (string.IsNullOrEmpty(a))
                return string.IsNullOrEmpty(b) ? 0 : b.Length;

            if (string.IsNullOrEmpty(b))
                return a.Length;

            var matrix = new int[a.Length + 1, b.Length + 1];

            for (int i = 0; i <= a.Length; i++)
                matrix[i, 0] = i;

            for (int j = 0; j <= b.Length; j++)
                matrix[0, j] = j;

            for (int i = 1; i <= a.Length; i++)
            {
                for (int j = 1; j <= b.Length; j++)
                {
                    int cost = (a[i - 1] == b[j - 1]) ? 0 : 1;

                    matrix[i, j] = Math.Min(
                        Math.Min(matrix[i - 1, j] + 1, matrix[i, j - 1] + 1),
                        matrix[i - 1, j - 1] + cost);
                }
            }

            return matrix[a.Length, b.Length];
        }

        private bool PositionsOverlapOrAdjacent(NamedEntity entity1, NamedEntity entity2)
        {
            // Check if entities overlap;
            if (entity1.StartIndex <= entity2.EndIndex && entity1.EndIndex >= entity2.StartIndex)
                return true;

            // Check if entities are adjacent (within 5 characters)
            var distance = Math.Min(
                Math.Abs(entity1.EndIndex - entity2.StartIndex),
                Math.Abs(entity2.EndIndex - entity1.StartIndex));

            return distance <= 5;
        }

        private List<string> ExtractKeywords()
        {
            var keywords = new List<string>();

            // Add normalized text;
            if (!string.IsNullOrWhiteSpace(NormalizedText))
                keywords.Add(NormalizedText);

            // Add type and subtype;
            keywords.Add(Type.ToString());
            if (!string.IsNullOrWhiteSpace(Subtype))
                keywords.Add(Subtype);

            // Add metadata values;
            foreach (var value in Metadata.Values)
            {
                if (value is string stringValue && !string.IsNullOrWhiteSpace(stringValue))
                    keywords.Add(stringValue.ToLowerInvariant());
            }

            // Add location keywords if available;
            if (GeoInfo != null)
            {
                if (!string.IsNullOrWhiteSpace(GeoInfo.Country))
                    keywords.Add(GeoInfo.Country.ToLowerInvariant());
                if (!string.IsNullOrWhiteSpace(GeoInfo.City))
                    keywords.Add(GeoInfo.City.ToLowerInvariant());
            }

            return keywords.Distinct().ToList();
        }

        #endregion;
    }

    #region Supporting Types and Enums;

    /// <summary>
    /// Named Entity types/categories;
    /// </summary>
    public enum EntityType;
    {
        // Core entity types;
        Person,
        Organization,
        Location,

        // Temporal entities;
        Date,
        Time,
        DateTime,

        // Numerical entities;
        Money,
        Percent,
        Quantity,
        Ordinal,
        Cardinal,

        // Other entities;
        Product,
        Event,
        WorkOfArt,
        Law,
        Language,

        // Special entities;
        Email,
        URL,
        PhoneNumber,
        IPAddress,

        // Custom/domain specific;
        Custom,

        // Unknown/unspecified;
        Unknown;
    }

    /// <summary>
    /// Temporal information for entities;
    /// </summary>
    public class TemporalInfo : ICloneable;
    {
        public DateTime? DateTimeValue { get; set; }
        public TimeSpan? TimeSpanValue { get; set; }
        public DateRange? DateRange { get; set; }
        public TemporalType TemporalType { get; set; }
        public string TimeZone { get; set; }
        public bool IsApproximate { get; set; }
        public string OriginalText { get; set; }
        public Dictionary<string, object> AdditionalInfo { get; set; }

        public TemporalInfo()
        {
            AdditionalInfo = new Dictionary<string, object>();
        }

        public TemporalInfo Clone()
        {
            return new TemporalInfo;
            {
                DateTimeValue = this.DateTimeValue,
                TimeSpanValue = this.TimeSpanValue,
                DateRange = this.DateRange?.Clone(),
                TemporalType = this.TemporalType,
                TimeZone = this.TimeZone,
                IsApproximate = this.IsApproximate,
                OriginalText = this.OriginalText,
                AdditionalInfo = new Dictionary<string, object>(this.AdditionalInfo)
            };
        }

        object ICloneable.Clone() => Clone();
    }

    /// <summary>
    /// Geographical information for entities;
    /// </summary>
    public class GeoInfo : ICloneable;
    {
        public double? Latitude { get; set; }
        public double? Longitude { get; set; }
        public string Country { get; set; }
        public string CountryCode { get; set; }
        public string Region { get; set; }
        public string City { get; set; }
        public string Address { get; set; }
        public string PostalCode { get; set; }
        public GeoAccuracy Accuracy { get; set; }
        public Dictionary<string, object> AdditionalInfo { get; set; }

        public GeoInfo()
        {
            AdditionalInfo = new Dictionary<string, object>();
        }

        public GeoInfo Clone()
        {
            return new GeoInfo;
            {
                Latitude = this.Latitude,
                Longitude = this.Longitude,
                Country = this.Country,
                CountryCode = this.CountryCode,
                Region = this.Region,
                City = this.City,
                Address = this.Address,
                PostalCode = this.PostalCode,
                Accuracy = this.Accuracy,
                AdditionalInfo = new Dictionary<string, object>(this.AdditionalInfo)
            };
        }

        object ICloneable.Clone() => Clone();
    }

    /// <summary>
    /// Numerical information for entities;
    /// </summary>
    public class NumericInfo : ICloneable;
    {
        public double NumericValue { get; set; }
        public string Unit { get; set; }
        public NumericType NumericType { get; set; }
        public double? MinValue { get; set; }
        public double? MaxValue { get; set; }
        public bool IsRange { get; set; }
        public string OriginalText { get; set; }
        public Dictionary<string, object> AdditionalInfo { get; set; }

        public NumericInfo()
        {
            AdditionalInfo = new Dictionary<string, object>();
        }

        public NumericInfo Clone()
        {
            return new NumericInfo;
            {
                NumericValue = this.NumericValue,
                Unit = this.Unit,
                NumericType = this.NumericType,
                MinValue = this.MinValue,
                MaxValue = this.MaxValue,
                IsRange = this.IsRange,
                OriginalText = this.OriginalText,
                AdditionalInfo = new Dictionary<string, object>(this.AdditionalInfo)
            };
        }

        object ICloneable.Clone() => Clone();
    }

    /// <summary>
    /// Entity relation information;
    /// </summary>
    public class EntityRelation : ICloneable;
    {
        public Guid SourceEntityId { get; set; }
        public Guid TargetEntityId { get; set; }
        public RelationType RelationType { get; set; }
        public string RelationName { get; set; }
        public double Confidence { get; set; }
        public Dictionary<string, object> Metadata { get; set; }
        public DateTime EstablishedAt { get; set; }

        public EntityRelation()
        {
            Metadata = new Dictionary<string, object>();
            EstablishedAt = DateTime.UtcNow;
        }

        public EntityRelation Clone()
        {
            return new EntityRelation;
            {
                SourceEntityId = this.SourceEntityId,
                TargetEntityId = this.TargetEntityId,
                RelationType = this.RelationType,
                RelationName = this.RelationName,
                Confidence = this.Confidence,
                Metadata = new Dictionary<string, object>(this.Metadata),
                EstablishedAt = this.EstablishedAt;
            };
        }

        object ICloneable.Clone() => Clone();
    }

    /// <summary>
    /// Disambiguation information for ambiguous entities;
    /// </summary>
    public class DisambiguationInfo : ICloneable;
    {
        public List<DisambiguationOption> Options { get; set; }
        public double AmbiguityScore { get; set; }
        public string SelectedOptionId { get; set; }
        public DisambiguationMethod Method { get; set; }
        public Dictionary<string, object> Context { get; set; }

        public DisambiguationInfo()
        {
            Options = new List<DisambiguationOption>();
            Context = new Dictionary<string, object>();
        }

        public DisambiguationInfo Clone()
        {
            return new DisambiguationInfo;
            {
                Options = this.Options.Select(o => o.Clone()).ToList(),
                AmbiguityScore = this.AmbiguityScore,
                SelectedOptionId = this.SelectedOptionId,
                Method = this.Method,
                Context = new Dictionary<string, object>(this.Context)
            };
        }

        object ICloneable.Clone() => Clone();
    }

    /// <summary>
    /// Entity sentiment information;
    /// </summary>
    public class EntitySentiment : ICloneable;
    {
        public double Polarity { get; set; } // -1.0 to 1.0;
        public double Subjectivity { get; set; } // 0.0 to 1.0;
        public EmotionType Emotion { get; set; }
        public double Intensity { get; set; } // 0.0 to 1.0;
        public Dictionary<string, double> EmotionScores { get; set; }

        public EntitySentiment()
        {
            EmotionScores = new Dictionary<string, double>();
        }

        public EntitySentiment Clone()
        {
            return new EntitySentiment;
            {
                Polarity = this.Polarity,
                Subjectivity = this.Subjectivity,
                Emotion = this.Emotion,
                Intensity = this.Intensity,
                EmotionScores = new Dictionary<string, double>(this.EmotionScores)
            };
        }

        object ICloneable.Clone() => Clone();
    }

    /// <summary>
    /// Entity source information;
    /// </summary>
    public class EntitySource : ICloneable;
    {
        public string DocumentId { get; set; }
        public string DocumentType { get; set; }
        public int ParagraphIndex { get; set; }
        public int SentenceIndex { get; set; }
        public string SourceUrl { get; set; }
        public DateTime? PublicationDate { get; set; }
        public string Author { get; set; }
        public string Publisher { get; set; }
        public Dictionary<string, object> Metadata { get; set; }

        public EntitySource()
        {
            Metadata = new Dictionary<string, object>();
        }

        public EntitySource Clone()
        {
            return new EntitySource;
            {
                DocumentId = this.DocumentId,
                DocumentType = this.DocumentType,
                ParagraphIndex = this.ParagraphIndex,
                SentenceIndex = this.SentenceIndex,
                SourceUrl = this.SourceUrl,
                PublicationDate = this.PublicationDate,
                Author = this.Author,
                Publisher = this.Publisher,
                Metadata = new Dictionary<string, object>(this.Metadata)
            };
        }

        object ICloneable.Clone() => Clone();
    }

    /// <summary>
    /// Entity index data for search and retrieval;
    /// </summary>
    public class EntityIndexData;
    {
        public Guid EntityId { get; set; }
        public string Text { get; set; }
        public string NormalizedText { get; set; }
        public EntityType Type { get; set; }
        public string Subtype { get; set; }
        public double Confidence { get; set; }
        public string Language { get; set; }
        public bool HasGeoInfo { get; set; }
        public bool HasTemporalInfo { get; set; }
        public bool HasNumericInfo { get; set; }
        public bool IsCoreEntity { get; set; }
        public double Score { get; set; }
        public List<string> Keywords { get; set; }
        public DateTime Timestamp { get; set; }
    }

    /// <summary>
    /// Simplified entity summary;
    /// </summary>
    public class EntitySummary;
    {
        public Guid Id { get; set; }
        public string Text { get; set; }
        public string Type { get; set; }
        public string Subtype { get; set; }
        public double Confidence { get; set; }
        public int StartIndex { get; set; }
        public int EndIndex { get; set; }
        public bool IsCoreEntity { get; set; }
        public bool HasGeoInfo { get; set; }
        public bool HasTemporalInfo { get; set; }
        public int RelationsCount { get; set; }
        public DateTime RecognizedAt { get; set; }
    }

    /// <summary>
    /// Entity merge strategies;
    /// </summary>
    public enum MergeStrategy;
    {
        PreferSource,
        PreferTarget,
        Intelligent,
        WeightedAverage;
    }

    /// <summary>
    /// Temporal types;
    /// </summary>
    public enum TemporalType;
    {
        Date,
        Time,
        DateTime,
        Duration,
        Range,
        Recurring;
    }

    /// <summary>
    /// Geographical accuracy levels;
    /// </summary>
    public enum GeoAccuracy;
    {
        Unknown,
        Country,
        Region,
        City,
        District,
        Street,
        Building,
        Exact;
    }

    /// <summary>
    /// Numerical types;
    /// </summary>
    public enum NumericType;
    {
        Integer,
        Decimal,
        Percentage,
        Currency,
        Measurement,
        Scientific;
    }

    /// <summary>
    /// Relation types between entities;
    /// </summary>
    public enum RelationType;
    {
        Unknown,
        PartOf,
        MemberOf,
        LocatedIn,
        WorksFor,
        FoundedBy,
        AcquiredBy,
        MarriedTo,
        ParentOf,
        ChildOf,
        SiblingOf,
        CreatedBy,
        Owns,
        Mentions,
        References,
        SimilarTo,
        OppositeOf,
        SynonymOf;
    }

    /// <summary>
    /// Disambiguation options;
    /// </summary>
    public class DisambiguationOption : ICloneable;
    {
        public string Id { get; set; }
        public string Text { get; set; }
        public string Description { get; set; }
        public string KnowledgeBaseUri { get; set; }
        public double Confidence { get; set; }
        public Dictionary<string, object> Metadata { get; set; }

        public DisambiguationOption()
        {
            Metadata = new Dictionary<string, object>();
        }

        public DisambiguationOption Clone()
        {
            return new DisambiguationOption;
            {
                Id = this.Id,
                Text = this.Text,
                Description = this.Description,
                KnowledgeBaseUri = this.KnowledgeBaseUri,
                Confidence = this.Confidence,
                Metadata = new Dictionary<string, object>(this.Metadata)
            };
        }

        object ICloneable.Clone() => Clone();
    }

    /// <summary>
    /// Disambiguation methods;
    /// </summary>
    public enum DisambiguationMethod;
    {
        None,
        ContextBased,
        KnowledgeBase,
        UserSelection,
        MachineLearning,
        Hybrid;
    }

    /// <summary>
    /// Emotion types;
    /// </summary>
    public enum EmotionType;
    {
        Neutral,
        Joy,
        Sadness,
        Anger,
        Fear,
        Surprise,
        Disgust,
        Trust,
        Anticipation;
    }

    /// <summary>
    /// Date range for temporal entities;
    /// </summary>
    public class DateRange : ICloneable;
    {
        public DateTime StartDate { get; set; }
        public DateTime EndDate { get; set; }
        public bool IsExact { get; set; }

        public DateRange Clone()
        {
            return new DateRange;
            {
                StartDate = this.StartDate,
                EndDate = this.EndDate,
                IsExact = this.IsExact;
            };
        }

        object ICloneable.Clone() => Clone();
    }

    /// <summary>
    /// Validation result for entities;
    /// </summary>
    public class ValidationResult;
    {
        public bool IsValid { get; set; }
        public List<string> Errors { get; set; }

        public ValidationResult()
        {
            Errors = new List<string>();
        }
    }

    #endregion;

    #region Extension Methods;

    public static class EntityTypeExtensions;
    {
        private static readonly Dictionary<EntityType, double> ImportanceWeights = new Dictionary<EntityType, double>
        {
            { EntityType.Person, 1.0 },
            { EntityType.Organization, 0.9 },
            { EntityType.Location, 0.8 },
            { EntityType.Product, 0.7 },
            { EntityType.Event, 0.7 },
            { EntityType.Date, 0.6 },
            { EntityType.Time, 0.5 },
            { EntityType.Money, 0.6 },
            { EntityType.Percent, 0.5 },
            { EntityType.Quantity, 0.4 },
            { EntityType.Email, 0.3 },
            { EntityType.URL, 0.3 },
            { EntityType.PhoneNumber, 0.3 },
            { EntityType.IPAddress, 0.3 },
            { EntityType.Unknown, 0.1 }
        };

        public static double GetImportanceWeight(this EntityType type)
        {
            return ImportanceWeights.TryGetValue(type, out var weight) ? weight : 0.5;
        }

        public static bool IsCoreEntityType(this EntityType type)
        {
            return type == EntityType.Person ||
                   type == EntityType.Organization ||
                   type == EntityType.Location;
        }

        public static bool IsTemporalEntity(this EntityType type)
        {
            return type == EntityType.Date ||
                   type == EntityType.Time ||
                   type == EntityType.DateTime;
        }

        public static bool IsNumericalEntity(this EntityType type)
        {
            return type == EntityType.Money ||
                   type == EntityType.Percent ||
                   type == EntityType.Quantity ||
                   type == EntityType.Ordinal ||
                   type == EntityType.Cardinal;
        }

        public static bool IsContactEntity(this EntityType type)
        {
            return type == EntityType.Email ||
                   type == EntityType.URL ||
                   type == EntityType.PhoneNumber ||
                   type == EntityType.IPAddress;
        }

        public static string GetDisplayName(this EntityType type)
        {
            return type switch;
            {
                EntityType.Person => "Person",
                EntityType.Organization => "Organization",
                EntityType.Location => "Location",
                EntityType.Date => "Date",
                EntityType.Time => "Time",
                EntityType.DateTime => "Date/Time",
                EntityType.Money => "Monetary Amount",
                EntityType.Percent => "Percentage",
                EntityType.Quantity => "Quantity",
                EntityType.Ordinal => "Ordinal Number",
                EntityType.Cardinal => "Cardinal Number",
                EntityType.Product => "Product",
                EntityType.Event => "Event",
                EntityType.WorkOfArt => "Work of Art",
                EntityType.Law => "Law/Legal",
                EntityType.Language => "Language",
                EntityType.Email => "Email Address",
                EntityType.URL => "URL/Website",
                EntityType.PhoneNumber => "Phone Number",
                EntityType.IPAddress => "IP Address",
                EntityType.Custom => "Custom Entity",
                EntityType.Unknown => "Unknown",
                _ => type.ToString()
            };
        }
    }

    #endregion;
}
