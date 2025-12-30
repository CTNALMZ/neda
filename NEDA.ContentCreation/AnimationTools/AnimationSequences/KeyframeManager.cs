using Microsoft.Extensions.Logging;
using NEDA.Animation.SequenceEditor.KeyframeEditing;
using NEDA.API.Middleware;
using NEDA.Core.Common.Utilities;
using NEDA.Core.ExceptionHandling.ErrorCodes;
using NEDA.Core.Logging;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using static NEDA.Brain.NLP_Engine.SyntaxAnalysis.GrammarEngine;

namespace NEDA.Animation.SequenceEditor.TimelineManagement;
{
    /// <summary>
    /// Keyframe yönetim sistemi - Animasyon keyframe'lerini yönetir;
    /// </summary>
    public interface IKeyframeManager;
    {
        /// <summary>
        /// Keyframe ekle;
        /// </summary>
        Task<Keyframe> AddKeyframeAsync(string animationId, Keyframe keyframe);

        /// <summary>
        /// Keyframe güncelle;
        /// </summary>
        Task<Keyframe> UpdateKeyframeAsync(string keyframeId, KeyframeUpdateRequest updateRequest);

        /// <summary>
        /// Keyframe sil;
        /// </summary>
        Task<bool> DeleteKeyframeAsync(string keyframeId);

        /// <summary>
        /// Timeline'daki keyframe'leri getir;
        /// </summary>
        Task<List<Keyframe>> GetTimelineKeyframesAsync(string timelineId);

        /// <summary>
        /// Keyframe'leri zaman bazlı sırala;
        /// </summary>
        Task<List<Keyframe>> SortKeyframesByTimeAsync(string animationId, bool ascending = true);

        /// <summary>
        /// Keyframe interpolation uygula;
        /// </summary>
        Task ApplyInterpolationAsync(string startKeyframeId, string endKeyframeId, InterpolationType interpolation);

        /// <summary>
        /// Keyframe kopyala;
        /// </summary>
        Task<Keyframe> CopyKeyframeAsync(string sourceKeyframeId, decimal targetTime);

        /// <summary>
        /// Keyframe offset uygula;
        /// </summary>
        Task OffsetKeyframesAsync(List<string> keyframeIds, TimeSpan offset);

        /// <summary>
        /// Bezier curve ayarla;
        /// </summary>
        Task SetBezierCurveAsync(string keyframeId, BezierCurve curve);

        /// <summary>
        /// Keyframe easing function ayarla;
        /// </summary>
        Task SetEasingFunctionAsync(string keyframeId, EasingFunction easingFunction);

        /// <summary>
        /// Keyframe snap to grid;
        /// </summary>
        Task SnapToGridAsync(List<string> keyframeIds, decimal gridSize);

        /// <summary>
        /// Keyframe gruplama;
        /// </summary>
        Task<KeyframeGroup> GroupKeyframesAsync(List<string> keyframeIds, string groupName);

        /// <summary>
        /// Keyframe değerini al;
        /// </summary>
        Task<object> GetKeyframeValueAsync(string keyframeId, string propertyName);

        /// <summary>
        /// Keyframe geçmişini getir;
        /// </summary>
        Task<List<KeyframeHistory>> GetKeyframeHistoryAsync(string keyframeId);
    }

    /// <summary>
    /// Keyframe yönetim implementasyonu;
    /// </summary>
    public class KeyframeManager : IKeyframeManager;
    {
        private readonly IKeyframeRepository _keyframeRepository;
        private readonly IAnimationRepository _animationRepository;
        private readonly ITimelineManager _timelineManager;
        private readonly IInterpolationEngine _interpolationEngine;
        private readonly ILogger<KeyframeManager> _logger;
        private readonly IErrorReporter _errorReporter;
        private readonly ICacheManager _cacheManager;

        public KeyframeManager(
            IKeyframeRepository keyframeRepository,
            IAnimationRepository animationRepository,
            ITimelineManager timelineManager,
            IInterpolationEngine interpolationEngine,
            ILogger<KeyframeManager> logger,
            IErrorReporter errorReporter,
            ICacheManager cacheManager)
        {
            _keyframeRepository = keyframeRepository ?? throw new ArgumentNullException(nameof(keyframeRepository));
            _animationRepository = animationRepository ?? throw new ArgumentNullException(nameof(animationRepository));
            _timelineManager = timelineManager ?? throw new ArgumentNullException(nameof(timelineManager));
            _interpolationEngine = interpolationEngine ?? throw new ArgumentNullException(nameof(interpolationEngine));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _errorReporter = errorReporter ?? throw new ArgumentNullException(nameof(errorReporter));
            _cacheManager = cacheManager ?? throw new ArgumentNullException(nameof(cacheManager));
        }

        /// <summary>
        /// Keyframe ekle;
        /// </summary>
        public async Task<Keyframe> AddKeyframeAsync(string animationId, Keyframe keyframe)
        {
            try
            {
                _logger.LogInformation($"Adding keyframe to animation {animationId} at time {keyframe.Time}");

                // Validasyon;
                ValidateKeyframe(keyframe);

                // Animasyon var mı kontrol et;
                var animation = await _animationRepository.GetByIdAsync(animationId);
                if (animation == null)
                {
                    throw new NEDAException(ErrorCode.AnimationNotFound, $"Animation {animationId} not found");
                }

                // Zaman çakışması kontrolü;
                var existingKeyframes = await _keyframeRepository.GetByAnimationIdAsync(animationId);
                var conflict = existingKeyframes.FirstOrDefault(k => Math.Abs(k.Time - keyframe.Time) < 0.001m);
                if (conflict != null)
                {
                    _logger.LogWarning($"Keyframe time conflict at {keyframe.Time}");
                    throw new NEDAException(ErrorCode.KeyframeTimeConflict,
                        $"Keyframe already exists at time {keyframe.Time}");
                }

                // Keyframe'i animasyona bağla;
                keyframe.AnimationId = animationId;
                keyframe.Id = Guid.NewGuid().ToString();
                keyframe.CreatedAt = DateTime.UtcNow;
                keyframe.UpdatedAt = DateTime.UtcNow;
                keyframe.Version = 1;

                // Bezier curve varsayılan değerleri;
                if (keyframe.Curve == null)
                {
                    keyframe.Curve = new BezierCurve;
                    {
                        HandleIn = new Vector2(0.25f, 0f),
                        HandleOut = new Vector2(0.75f, 1f),
                        CurveType = CurveType.Bezier;
                    };
                }

                // Easing function varsayılan;
                if (keyframe.EasingFunction == EasingFunction.None)
                {
                    keyframe.EasingFunction = EasingFunction.Linear;
                }

                // Veritabanına kaydet;
                var result = await _keyframeRepository.AddAsync(keyframe);

                // Cache'i güncelle;
                await UpdateAnimationCache(animationId);

                // Timeline'ı güncelle;
                await _timelineManager.NotifyKeyframeAddedAsync(animation.TimelineId, result);

                _logger.LogInformation($"Keyframe {result.Id} added successfully");
                return result;
            }
            catch (NEDAException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error adding keyframe to animation {animationId}");
                await _errorReporter.ReportErrorAsync(ex, ErrorSeverity.High, new { animationId, keyframe });
                throw new NEDAException(ErrorCode.KeyframeAddFailed, "Failed to add keyframe", ex);
            }
        }

        /// <summary>
        /// Keyframe güncelle;
        /// </summary>
        public async Task<Keyframe> UpdateKeyframeAsync(string keyframeId, KeyframeUpdateRequest updateRequest)
        {
            try
            {
                _logger.LogInformation($"Updating keyframe {keyframeId}");

                // Keyframe'i getir;
                var keyframe = await _keyframeRepository.GetByIdAsync(keyframeId);
                if (keyframe == null)
                {
                    throw new NEDAException(ErrorCode.KeyframeNotFound, $"Keyframe {keyframeId} not found");
                }

                // Geçmişi kaydet;
                await SaveKeyframeHistory(keyframe);

                // Güncelleme uygula;
                if (updateRequest.Time.HasValue)
                {
                    // Zaman çakışması kontrolü;
                    var existingKeyframes = await _keyframeRepository.GetByAnimationIdAsync(keyframe.AnimationId);
                    var conflict = existingKeyframes;
                        .Where(k => k.Id != keyframeId)
                        .FirstOrDefault(k => Math.Abs(k.Time - updateRequest.Time.Value) < 0.001m);

                    if (conflict != null)
                    {
                        throw new NEDAException(ErrorCode.KeyframeTimeConflict,
                            $"Keyframe already exists at time {updateRequest.Time.Value}");
                    }

                    keyframe.Time = updateRequest.Time.Value;
                }

                if (updateRequest.Values != null)
                {
                    keyframe.Values = MergeKeyframeValues(keyframe.Values, updateRequest.Values);
                }

                if (updateRequest.Curve != null)
                {
                    keyframe.Curve = updateRequest.Curve;
                }

                if (updateRequest.EasingFunction.HasValue)
                {
                    keyframe.EasingFunction = updateRequest.EasingFunction.Value;
                }

                if (updateRequest.Selected.HasValue)
                {
                    keyframe.Selected = updateRequest.Selected.Value;
                }

                if (updateRequest.Locked.HasValue)
                {
                    keyframe.Locked = updateRequest.Locked.Value;
                }

                if (!string.IsNullOrEmpty(updateRequest.Notes))
                {
                    keyframe.Notes = updateRequest.Notes;
                }

                keyframe.UpdatedAt = DateTime.UtcNow;
                keyframe.Version++;

                // Güncelle;
                var result = await _keyframeRepository.UpdateAsync(keyframe);

                // Cache'i güncelle;
                await UpdateAnimationCache(keyframe.AnimationId);

                // Timeline'ı güncelle;
                var animation = await _animationRepository.GetByIdAsync(keyframe.AnimationId);
                if (animation != null)
                {
                    await _timelineManager.NotifyKeyframeUpdatedAsync(animation.TimelineId, result);
                }

                _logger.LogInformation($"Keyframe {keyframeId} updated successfully");
                return result;
            }
            catch (NEDAException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error updating keyframe {keyframeId}");
                await _errorReporter.ReportErrorAsync(ex, ErrorSeverity.Medium, new { keyframeId, updateRequest });
                throw new NEDAException(ErrorCode.KeyframeUpdateFailed, "Failed to update keyframe", ex);
            }
        }

        /// <summary>
        /// Keyframe sil;
        /// </summary>
        public async Task<bool> DeleteKeyframeAsync(string keyframeId)
        {
            try
            {
                _logger.LogInformation($"Deleting keyframe {keyframeId}");

                // Keyframe'i getir;
                var keyframe = await _keyframeRepository.GetByIdAsync(keyframeId);
                if (keyframe == null)
                {
                    throw new NEDAException(ErrorCode.KeyframeNotFound, $"Keyframe {keyframeId} not found");
                }

                if (keyframe.Locked)
                {
                    throw new NEDAException(ErrorCode.KeyframeLocked, "Cannot delete locked keyframe");
                }

                // Sil;
                var success = await _keyframeRepository.DeleteAsync(keyframeId);

                if (success)
                {
                    // Cache'i güncelle;
                    await UpdateAnimationCache(keyframe.AnimationId);

                    // Timeline'ı güncelle;
                    var animation = await _animationRepository.GetByIdAsync(keyframe.AnimationId);
                    if (animation != null)
                    {
                        await _timelineManager.NotifyKeyframeDeletedAsync(animation.TimelineId, keyframeId);
                    }

                    _logger.LogInformation($"Keyframe {keyframeId} deleted successfully");
                }

                return success;
            }
            catch (NEDAException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error deleting keyframe {keyframeId}");
                await _errorReporter.ReportErrorAsync(ex, ErrorSeverity.Medium, new { keyframeId });
                throw new NEDAException(ErrorCode.KeyframeDeleteFailed, "Failed to delete keyframe", ex);
            }
        }

        /// <summary>
        /// Timeline'daki keyframe'leri getir;
        /// </summary>
        public async Task<List<Keyframe>> GetTimelineKeyframesAsync(string timelineId)
        {
            try
            {
                // Cache'den kontrol et;
                var cacheKey = $"timeline_keyframes_{timelineId}";
                var cached = await _cacheManager.GetAsync<List<Keyframe>>(cacheKey);
                if (cached != null)
                {
                    return cached;
                }

                // Timeline'daki animasyonları getir;
                var animations = await _animationRepository.GetByTimelineIdAsync(timelineId);
                var keyframes = new List<Keyframe>();

                foreach (var animation in animations)
                {
                    var animationKeyframes = await _keyframeRepository.GetByAnimationIdAsync(animation.Id);
                    keyframes.AddRange(animationKeyframes);
                }

                // Zaman sırasına göre sırala;
                keyframes = keyframes.OrderBy(k => k.Time).ToList();

                // Cache'e kaydet;
                await _cacheManager.SetAsync(cacheKey, keyframes, TimeSpan.FromMinutes(5));

                return keyframes;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error getting timeline keyframes for timeline {timelineId}");
                await _errorReporter.ReportErrorAsync(ex, ErrorSeverity.Low, new { timelineId });
                throw new NEDAException(ErrorCode.KeyframeRetrievalFailed, "Failed to retrieve keyframes", ex);
            }
        }

        /// <summary>
        /// Keyframe'leri zaman bazlı sırala;
        /// </summary>
        public async Task<List<Keyframe>> SortKeyframesByTimeAsync(string animationId, bool ascending = true)
        {
            try
            {
                var keyframes = await _keyframeRepository.GetByAnimationIdAsync(animationId);

                return ascending;
                    ? keyframes.OrderBy(k => k.Time).ToList()
                    : keyframes.OrderByDescending(k => k.Time).ToList();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error sorting keyframes for animation {animationId}");
                await _errorReporter.ReportErrorAsync(ex, ErrorSeverity.Low, new { animationId, ascending });
                throw new NEDAException(ErrorCode.KeyframeSortFailed, "Failed to sort keyframes", ex);
            }
        }

        /// <summary>
        /// Keyframe interpolation uygula;
        /// </summary>
        public async Task ApplyInterpolationAsync(string startKeyframeId, string endKeyframeId, InterpolationType interpolation)
        {
            try
            {
                _logger.LogInformation($"Applying {interpolation} interpolation between {startKeyframeId} and {endKeyframeId}");

                var startKeyframe = await _keyframeRepository.GetByIdAsync(startKeyframeId);
                var endKeyframe = await _keyframeRepository.GetByIdAsync(endKeyframeId);

                if (startKeyframe == null || endKeyframe == null)
                {
                    throw new NEDAException(ErrorCode.KeyframeNotFound, "One or both keyframes not found");
                }

                if (startKeyframe.AnimationId != endKeyframe.AnimationId)
                {
                    throw new NEDAException(ErrorCode.KeyframeAnimationMismatch,
                        "Keyframes must belong to the same animation");
                }

                // Interpolation uygula;
                await _interpolationEngine.ApplyInterpolationAsync(startKeyframe, endKeyframe, interpolation);

                // Timeline'ı güncelle;
                var animation = await _animationRepository.GetByIdAsync(startKeyframe.AnimationId);
                if (animation != null)
                {
                    await _timelineManager.NotifyInterpolationAppliedAsync(animation.TimelineId,
                        startKeyframeId, endKeyframeId, interpolation);
                }

                _logger.LogInformation($"Interpolation applied successfully");
            }
            catch (NEDAException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error applying interpolation");
                await _errorReporter.ReportErrorAsync(ex, ErrorSeverity.Medium,
                    new { startKeyframeId, endKeyframeId, interpolation });
                throw new NEDAException(ErrorCode.InterpolationFailed, "Failed to apply interpolation", ex);
            }
        }

        /// <summary>
        /// Keyframe kopyala;
        /// </summary>
        public async Task<Keyframe> CopyKeyframeAsync(string sourceKeyframeId, decimal targetTime)
        {
            try
            {
                _logger.LogInformation($"Copying keyframe {sourceKeyframeId} to time {targetTime}");

                var sourceKeyframe = await _keyframeRepository.GetByIdAsync(sourceKeyframeId);
                if (sourceKeyframe == null)
                {
                    throw new NEDAException(ErrorCode.KeyframeNotFound, $"Keyframe {sourceKeyframeId} not found");
                }

                // Yeni keyframe oluştur;
                var newKeyframe = new Keyframe;
                {
                    Id = Guid.NewGuid().ToString(),
                    AnimationId = sourceKeyframe.AnimationId,
                    Time = targetTime,
                    Values = DeepCopyValues(sourceKeyframe.Values),
                    Curve = sourceKeyframe.Curve?.Clone(),
                    EasingFunction = sourceKeyframe.EasingFunction,
                    Selected = false,
                    Locked = false,
                    Notes = $"Copied from {sourceKeyframeId}",
                    CreatedAt = DateTime.UtcNow,
                    UpdatedAt = DateTime.UtcNow,
                    Version = 1;
                };

                // Zaman çakışması kontrolü;
                var existingKeyframes = await _keyframeRepository.GetByAnimationIdAsync(sourceKeyframe.AnimationId);
                var conflict = existingKeyframes.FirstOrDefault(k => Math.Abs(k.Time - targetTime) < 0.001m);
                if (conflict != null)
                {
                    throw new NEDAException(ErrorCode.KeyframeTimeConflict,
                        $"Keyframe already exists at time {targetTime}");
                }

                // Kaydet;
                var result = await _keyframeRepository.AddAsync(newKeyframe);

                // Cache'i güncelle;
                await UpdateAnimationCache(sourceKeyframe.AnimationId);

                _logger.LogInformation($"Keyframe copied successfully to {result.Id}");
                return result;
            }
            catch (NEDAException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error copying keyframe {sourceKeyframeId}");
                await _errorReporter.ReportErrorAsync(ex, ErrorSeverity.Medium,
                    new { sourceKeyframeId, targetTime });
                throw new NEDAException(ErrorCode.KeyframeCopyFailed, "Failed to copy keyframe", ex);
            }
        }

        /// <summary>
        /// Keyframe offset uygula;
        /// </summary>
        public async Task OffsetKeyframesAsync(List<string> keyframeIds, TimeSpan offset)
        {
            try
            {
                _logger.LogInformation($"Applying offset {offset} to {keyframeIds.Count} keyframes");

                var keyframes = new List<Keyframe>();
                string animationId = null;

                // Tüm keyframe'leri getir ve animasyon ID'sini kontrol et;
                foreach (var id in keyframeIds)
                {
                    var keyframe = await _keyframeRepository.GetByIdAsync(id);
                    if (keyframe == null)
                    {
                        throw new NEDAException(ErrorCode.KeyframeNotFound, $"Keyframe {id} not found");
                    }

                    if (animationId == null)
                    {
                        animationId = keyframe.AnimationId;
                    }
                    else if (keyframe.AnimationId != animationId)
                    {
                        throw new NEDAException(ErrorCode.KeyframeAnimationMismatch,
                            "All keyframes must belong to the same animation");
                    }

                    keyframes.Add(keyframe);
                }

                // Offset uygula;
                foreach (var keyframe in keyframes)
                {
                    if (!keyframe.Locked)
                    {
                        // Geçmişi kaydet;
                        await SaveKeyframeHistory(keyframe);

                        // Zamanı güncelle;
                        keyframe.Time += (decimal)offset.TotalSeconds;
                        keyframe.UpdatedAt = DateTime.UtcNow;
                        keyframe.Version++;

                        await _keyframeRepository.UpdateAsync(keyframe);
                    }
                }

                // Cache'i güncelle;
                await UpdateAnimationCache(animationId);

                // Timeline'ı güncelle;
                var animation = await _animationRepository.GetByIdAsync(animationId);
                if (animation != null)
                {
                    await _timelineManager.NotifyKeyframesOffsetAsync(animation.TimelineId, keyframeIds, offset);
                }

                _logger.LogInformation($"Offset applied successfully");
            }
            catch (NEDAException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error applying offset to keyframes");
                await _errorReporter.ReportErrorAsync(ex, ErrorSeverity.Medium,
                    new { keyframeIds, offset });
                throw new NEDAException(ErrorCode.KeyframeOffsetFailed, "Failed to apply offset", ex);
            }
        }

        /// <summary>
        /// Bezier curve ayarla;
        /// </summary>
        public async Task SetBezierCurveAsync(string keyframeId, BezierCurve curve)
        {
            try
            {
                var keyframe = await _keyframeRepository.GetByIdAsync(keyframeId);
                if (keyframe == null)
                {
                    throw new NEDAException(ErrorCode.KeyframeNotFound, $"Keyframe {keyframeId} not found");
                }

                if (keyframe.Locked)
                {
                    throw new NEDAException(ErrorCode.KeyframeLocked, "Cannot modify locked keyframe");
                }

                // Geçmişi kaydet;
                await SaveKeyframeHistory(keyframe);

                // Curve'ü güncelle;
                keyframe.Curve = curve;
                keyframe.UpdatedAt = DateTime.UtcNow;
                keyframe.Version++;

                await _keyframeRepository.UpdateAsync(keyframe);

                _logger.LogInformation($"Bezier curve set for keyframe {keyframeId}");
            }
            catch (NEDAException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error setting bezier curve for keyframe {keyframeId}");
                await _errorReporter.ReportErrorAsync(ex, ErrorSeverity.Medium,
                    new { keyframeId, curve });
                throw new NEDAException(ErrorCode.BezierCurveSetFailed, "Failed to set bezier curve", ex);
            }
        }

        /// <summary>
        /// Keyframe easing function ayarla;
        /// </summary>
        public async Task SetEasingFunctionAsync(string keyframeId, EasingFunction easingFunction)
        {
            try
            {
                var keyframe = await _keyframeRepository.GetByIdAsync(keyframeId);
                if (keyframe == null)
                {
                    throw new NEDAException(ErrorCode.KeyframeNotFound, $"Keyframe {keyframeId} not found");
                }

                if (keyframe.Locked)
                {
                    throw new NEDAException(ErrorCode.KeyframeLocked, "Cannot modify locked keyframe");
                }

                // Geçmişi kaydet;
                await SaveKeyframeHistory(keyframe);

                // Easing function güncelle;
                keyframe.EasingFunction = easingFunction;
                keyframe.UpdatedAt = DateTime.UtcNow;
                keyframe.Version++;

                await _keyframeRepository.UpdateAsync(keyframe);

                _logger.LogInformation($"Easing function set to {easingFunction} for keyframe {keyframeId}");
            }
            catch (NEDAException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error setting easing function for keyframe {keyframeId}");
                await _errorReporter.ReportErrorAsync(ex, ErrorSeverity.Medium,
                    new { keyframeId, easingFunction });
                throw new NEDAException(ErrorCode.EasingFunctionSetFailed, "Failed to set easing function", ex);
            }
        }

        /// <summary>
        /// Keyframe snap to grid;
        /// </summary>
        public async Task SnapToGridAsync(List<string> keyframeIds, decimal gridSize)
        {
            try
            {
                _logger.LogInformation($"Snapping {keyframeIds.Count} keyframes to grid size {gridSize}");

                foreach (var keyframeId in keyframeIds)
                {
                    var keyframe = await _keyframeRepository.GetByIdAsync(keyframeId);
                    if (keyframe != null && !keyframe.Locked)
                    {
                        // Geçmişi kaydet;
                        await SaveKeyframeHistory(keyframe);

                        // Grid'e snap et;
                        keyframe.Time = Math.Round(keyframe.Time / gridSize) * gridSize;
                        keyframe.UpdatedAt = DateTime.UtcNow;
                        keyframe.Version++;

                        await _keyframeRepository.UpdateAsync(keyframe);
                    }
                }

                // Cache'i güncelle (ilk keyframe'in animasyon ID'sini kullan)
                if (keyframeIds.Count > 0)
                {
                    var firstKeyframe = await _keyframeRepository.GetByIdAsync(keyframeIds[0]);
                    if (firstKeyframe != null)
                    {
                        await UpdateAnimationCache(firstKeyframe.AnimationId);
                    }
                }

                _logger.LogInformation($"Snap to grid completed");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error snapping keyframes to grid");
                await _errorReporter.ReportErrorAsync(ex, ErrorSeverity.Low,
                    new { keyframeIds, gridSize });
                throw new NEDAException(ErrorCode.SnapToGridFailed, "Failed to snap to grid", ex);
            }
        }

        /// <summary>
        /// Keyframe gruplama;
        /// </summary>
        public async Task<KeyframeGroup> GroupKeyframesAsync(List<string> keyframeIds, string groupName)
        {
            try
            {
                _logger.LogInformation($"Grouping {keyframeIds.Count} keyframes into '{groupName}'");

                var keyframes = new List<Keyframe>();
                string animationId = null;

                // Tüm keyframe'leri getir;
                foreach (var id in keyframeIds)
                {
                    var keyframe = await _keyframeRepository.GetByIdAsync(id);
                    if (keyframe == null)
                    {
                        throw new NEDAException(ErrorCode.KeyframeNotFound, $"Keyframe {id} not found");
                    }

                    if (animationId == null)
                    {
                        animationId = keyframe.AnimationId;
                    }
                    else if (keyframe.AnimationId != animationId)
                    {
                        throw new NEDAException(ErrorCode.KeyframeAnimationMismatch,
                            "All keyframes must belong to the same animation");
                    }

                    keyframes.Add(keyframe);
                }

                // Grup oluştur;
                var group = new KeyframeGroup;
                {
                    Id = Guid.NewGuid().ToString(),
                    Name = groupName,
                    AnimationId = animationId,
                    KeyframeIds = keyframeIds,
                    CreatedAt = DateTime.UtcNow,
                    UpdatedAt = DateTime.UtcNow;
                };

                // Keyframe'leri grupla;
                foreach (var keyframe in keyframes)
                {
                    if (!keyframe.Locked)
                    {
                        keyframe.GroupId = group.Id;
                        keyframe.UpdatedAt = DateTime.UtcNow;
                        await _keyframeRepository.UpdateAsync(keyframe);
                    }
                }

                // Grubu kaydet (repository eklenecek)
                // await _keyframeGroupRepository.AddAsync(group);

                _logger.LogInformation($"Keyframes grouped successfully into {group.Id}");
                return group;
            }
            catch (NEDAException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error grouping keyframes");
                await _errorReporter.ReportErrorAsync(ex, ErrorSeverity.Medium,
                    new { keyframeIds, groupName });
                throw new NEDAException(ErrorCode.KeyframeGroupFailed, "Failed to group keyframes", ex);
            }
        }

        /// <summary>
        /// Keyframe değerini al;
        /// </summary>
        public async Task<object> GetKeyframeValueAsync(string keyframeId, string propertyName)
        {
            try
            {
                var keyframe = await _keyframeRepository.GetByIdAsync(keyframeId);
                if (keyframe == null)
                {
                    throw new NEDAException(ErrorCode.KeyframeNotFound, $"Keyframe {keyframeId} not found");
                }

                if (keyframe.Values.TryGetValue(propertyName, out var value))
                {
                    return value;
                }

                throw new NEDAException(ErrorCode.PropertyNotFound,
                    $"Property '{propertyName}' not found in keyframe {keyframeId}");
            }
            catch (NEDAException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error getting keyframe value for {propertyName}");
                await _errorReporter.ReportErrorAsync(ex, ErrorSeverity.Low,
                    new { keyframeId, propertyName });
                throw new NEDAException(ErrorCode.KeyframeValueRetrievalFailed,
                    "Failed to retrieve keyframe value", ex);
            }
        }

        /// <summary>
        /// Keyframe geçmişini getir;
        /// </summary>
        public async Task<List<KeyframeHistory>> GetKeyframeHistoryAsync(string keyframeId)
        {
            try
            {
                // Keyframe var mı kontrol et;
                var keyframe = await _keyframeRepository.GetByIdAsync(keyframeId);
                if (keyframe == null)
                {
                    throw new NEDAException(ErrorCode.KeyframeNotFound, $"Keyframe {keyframeId} not found");
                }

                // Geçmişi getir (repository eklenecek)
                // return await _keyframeHistoryRepository.GetByKeyframeIdAsync(keyframeId);

                return new List<KeyframeHistory>(); // Placeholder;
            }
            catch (NEDAException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error getting keyframe history for {keyframeId}");
                await _errorReporter.ReportErrorAsync(ex, ErrorSeverity.Low, new { keyframeId });
                throw new NEDAException(ErrorCode.HistoryRetrievalFailed,
                    "Failed to retrieve keyframe history", ex);
            }
        }

        #region Private Methods;

        private void ValidateKeyframe(Keyframe keyframe)
        {
            if (keyframe == null)
            {
                throw new ArgumentNullException(nameof(keyframe));
            }

            if (keyframe.Time < 0)
            {
                throw new NEDAException(ErrorCode.InvalidKeyframeTime, "Keyframe time cannot be negative");
            }

            if (keyframe.Values == null || keyframe.Values.Count == 0)
            {
                throw new NEDAException(ErrorCode.InvalidKeyframeValues, "Keyframe must have values");
            }
        }

        private Dictionary<string, object> MergeKeyframeValues(
            Dictionary<string, object> existingValues,
            Dictionary<string, object> newValues)
        {
            var merged = new Dictionary<string, object>(existingValues);

            foreach (var kvp in newValues)
            {
                merged[kvp.Key] = kvp.Value;
            }

            return merged;
        }

        private Dictionary<string, object> DeepCopyValues(Dictionary<string, object> source)
        {
            if (source == null) return null;

            var copy = new Dictionary<string, object>();
            foreach (var kvp in source)
            {
                // Basit deep copy (gerçek implementasyonda daha kompleks olabilir)
                if (kvp.Value is ICloneable cloneable)
                {
                    copy[kvp.Key] = cloneable.Clone();
                }
                else if (kvp.Value is ValueType)
                {
                    copy[kvp.Key] = kvp.Value;
                }
                else;
                {
                    // Serialize/deserialize gerekebilir;
                    copy[kvp.Key] = kvp.Value;
                }
            }
            return copy;
        }

        private async Task SaveKeyframeHistory(Keyframe keyframe)
        {
            try
            {
                var history = new KeyframeHistory;
                {
                    Id = Guid.NewGuid().ToString(),
                    KeyframeId = keyframe.Id,
                    Time = keyframe.Time,
                    Values = DeepCopyValues(keyframe.Values),
                    Curve = keyframe.Curve?.Clone(),
                    EasingFunction = keyframe.EasingFunction,
                    SavedAt = DateTime.UtcNow,
                    Version = keyframe.Version,
                    UserId = "system" // Gerçek implementasyonda current user;
                };

                // Geçmişi kaydet (repository eklenecek)
                // await _keyframeHistoryRepository.AddAsync(history);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, $"Failed to save keyframe history for {keyframe.Id}");
                // History kaydetme hatası ana işlemi durdurmamalı;
            }
        }

        private async Task UpdateAnimationCache(string animationId)
        {
            try
            {
                var cacheKey = $"animation_keyframes_{animationId}";
                await _cacheManager.RemoveAsync(cacheKey);

                // Timeline cache'ini de temizle;
                var animation = await _animationRepository.GetByIdAsync(animationId);
                if (animation != null)
                {
                    var timelineCacheKey = $"timeline_keyframes_{animation.TimelineId}";
                    await _cacheManager.RemoveAsync(timelineCacheKey);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, $"Failed to update cache for animation {animationId}");
            }
        }

        #endregion;
    }

    #region Supporting Classes;

    /// <summary>
    /// Keyframe model;
    /// </summary>
    public class Keyframe;
    {
        public string Id { get; set; }
        public string AnimationId { get; set; }
        public string GroupId { get; set; }
        public decimal Time { get; set; }
        public Dictionary<string, object> Values { get; set; }
        public BezierCurve Curve { get; set; }
        public EasingFunction EasingFunction { get; set; }
        public bool Selected { get; set; }
        public bool Locked { get; set; }
        public string Notes { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime UpdatedAt { get; set; }
        public int Version { get; set; }
    }

    /// <summary>
    /// Keyframe güncelleme isteği;
    /// </summary>
    public class KeyframeUpdateRequest;
    {
        public decimal? Time { get; set; }
        public Dictionary<string, object> Values { get; set; }
        public BezierCurve Curve { get; set; }
        public EasingFunction? EasingFunction { get; set; }
        public bool? Selected { get; set; }
        public bool? Locked { get; set; }
        public string Notes { get; set; }
    }

    /// <summary>
    /// Bezier eğrisi;
    /// </summary>
    public class BezierCurve : ICloneable;
    {
        public Vector2 HandleIn { get; set; }
        public Vector2 HandleOut { get; set; }
        public CurveType CurveType { get; set; }

        public object Clone()
        {
            return new BezierCurve;
            {
                HandleIn = this.HandleIn,
                HandleOut = this.HandleOut,
                CurveType = this.CurveType;
            };
        }
    }

    /// <summary>
    /// 2D vektör;
    /// </summary>
    public struct Vector2;
    {
        public float X { get; set; }
        public float Y { get; set; }

        public Vector2(float x, float y)
        {
            X = x;
            Y = y;
        }
    }

    /// <summary>
    /// Eğri tipi;
    /// </summary>
    public enum CurveType;
    {
        Linear,
        Bezier,
        Hold,
        EaseIn,
        EaseOut,
        EaseInOut;
    }

    /// <summary>
    /// Easing function tipi;
    /// </summary>
    public enum EasingFunction;
    {
        None,
        Linear,
        EaseInQuad,
        EaseOutQuad,
        EaseInOutQuad,
        EaseInCubic,
        EaseOutCubic,
        EaseInOutCubic,
        EaseInQuart,
        EaseOutQuart,
        EaseInOutQuart,
        EaseInQuint,
        EaseOutQuint,
        EaseInOutQuint,
        EaseInSine,
        EaseOutSine,
        EaseInOutSine,
        EaseInExpo,
        EaseOutExpo,
        EaseInOutExpo,
        EaseInCirc,
        EaseOutCirc,
        EaseInOutCirc,
        Bounce,
        Elastic;
    }

    /// <summary>
    /// Interpolation tipi;
    /// </summary>
    public enum InterpolationType;
    {
        Linear,
        Bezier,
        Step,
        Smooth;
    }

    /// <summary>
    /// Keyframe grubu;
    /// </summary>
    public class KeyframeGroup;
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string AnimationId { get; set; }
        public List<string> KeyframeIds { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime UpdatedAt { get; set; }
    }

    /// <summary>
    /// Keyframe geçmişi;
    /// </summary>
    public class KeyframeHistory;
    {
        public string Id { get; set; }
        public string KeyframeId { get; set; }
        public decimal Time { get; set; }
        public Dictionary<string, object> Values { get; set; }
        public BezierCurve Curve { get; set; }
        public EasingFunction EasingFunction { get; set; }
        public DateTime SavedAt { get; set; }
        public int Version { get; set; }
        public string UserId { get; set; }
    }

    #endregion;
}
