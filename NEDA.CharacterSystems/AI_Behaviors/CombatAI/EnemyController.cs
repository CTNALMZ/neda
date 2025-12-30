using NEDA.Brain.NLP_Engine.EntityRecognition;
using NEDA.CharacterSystems.AI_Behaviors.BehaviorTrees;
using NEDA.CharacterSystems.AI_Behaviors.BlackboardSystems;
using NEDA.CharacterSystems.GameplaySystems.InventoryManager;
using NEDA.Core.Engine;
using NEDA.Core.Logging;
using NEDA.Core.Security;
using NEDA.NeuralNetwork.DeepLearning;
using NEDA.NeuralNetwork.PatternRecognition;
using System;
using System.Collections.Generic;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;

namespace NEDA.CharacterSystems.AI_Behaviors.CombatAI;
{
    /// <summary>
    /// Düşman AI kontrol sistemi - Gelişmiş savaş davranışları, taktiksel karar verme ve adaptif öğrenme;
    /// </summary>
    public class EnemyController : IDisposable
    {
        #region Fields and Properties;

        private readonly ILogger _logger;
        private readonly AIEngine _aiEngine;
        private readonly Blackboard _blackboard;
        private BehaviorTree _behaviorTree;
        private NeuralNetwork _neuralNetwork;
        private readonly ThreatDetector _threatDetector;
        private readonly TacticalAI _tacticalAI;
        private readonly CombatSystem _combatSystem;

        private EnemyState _currentState;
        private EnemyConfiguration _configuration;
        private CancellationTokenSource _aiLoopTokenSource;
        private bool _isInitialized;
        private readonly object _stateLock = new object();

        // Public Properties;
        public string EnemyId { get; private set; }
        public EnemyType EnemyType { get; private set; }
        public EnemyDifficulty Difficulty { get; set; }
        public float Health { get; private set; }
        public float MaxHealth { get; private set; }
        public bool IsAlive => Health > 0;
        public Vector3 Position { get; private set; }
        public Quaternion Rotation { get; private set; }
        public Inventory Inventory { get; private set; }
        public List<EnemyAbility> Abilities { get; private set; }
        public AggressionLevel Aggression { get; private set; }
        public Dictionary<string, float> ThreatAssessment { get; private set; }
        public EnemyMemory Memory { get; private set; }

        #endregion;

        #region Constructors;

        /// <summary>
        /// EnemyController başlatıcı;
        /// </summary>
        public EnemyController(string enemyId, EnemyType type, EnemyConfiguration config)
        {
            EnemyId = enemyId ?? throw new ArgumentNullException(nameof(enemyId));
            EnemyType = type;
            _configuration = config ?? throw new ArgumentNullException(nameof(config));

            // Bağımlılık enjeksiyonu;
            _logger = LogManager.GetLogger($"EnemyController_{enemyId}");
            _aiEngine = AIEngine.Instance;
            _blackboard = new Blackboard(enemyId);
            _threatDetector = new ThreatDetector();
            _tacticalAI = new TacticalAI();
            _combatSystem = new CombatSystem();

            InitializeProperties();
        }

        /// <summary>
        /// Önceden kaydedilmiş durumdan EnemyController oluşturur;
        /// </summary>
        public EnemyController(EnemySaveState saveState)
        {
            if (saveState == null) throw new ArgumentNullException(nameof(saveState));

            EnemyId = saveState.EnemyId;
            EnemyType = saveState.EnemyType;
            _configuration = saveState.Configuration;

            _logger = LogManager.GetLogger($"EnemyController_{saveState.EnemyId}");
            _aiEngine = AIEngine.Instance;
            _blackboard = new Blackboard(saveState.EnemyId);
            _threatDetector = new ThreatDetector();
            _tacticalAI = new TacticalAI();
            _combatSystem = new CombatSystem();

            LoadFromSaveState(saveState);
        }

        #endregion;

        #region Initialization;

        /// <summary>
        /// Düşman kontrolcüsünü başlatır;
        /// </summary>
        public async Task InitializeAsync(CancellationToken cancellationToken = default)
        {
            if (_isInitialized) return;

            try
            {
                _logger.Info($"Initializing EnemyController: {EnemyId} ({EnemyType})");

                // Temel özellikleri ayarla;
                Health = MaxHealth;
                Aggression = _configuration.BaseAggression;

                // Inventory oluştur;
                Inventory = new Inventory(_configuration.InventorySize);
                await InitializeInventoryAsync(cancellationToken);

                // Yetenekleri yükle;
                Abilities = await LoadAbilitiesAsync(cancellationToken);

                // Behavior Tree oluştur;
                _behaviorTree = CreateBehaviorTree();

                // Neural Network başlat;
                _neuralNetwork = CreateNeuralNetwork();
                await _neuralNetwork.InitializeAsync();

                // Threat assessment dictionary;
                ThreatAssessment = new Dictionary<string, float>();

                // Bellek sistemi;
                Memory = new EnemyMemory(_configuration.MemoryCapacity);

                // AI döngüsünü başlat;
                _aiLoopTokenSource = new CancellationTokenSource();
                StartAILoop(_aiLoopTokenSource.Token);

                _isInitialized = true;
                _logger.Info($"EnemyController initialized successfully: {EnemyId}");
            }
            catch (Exception ex)
            {
                _logger.Error($"Failed to initialize EnemyController {EnemyId}: {ex.Message}", ex);
                throw new EnemyControllerException($"Initialization failed for enemy {EnemyId}", ex);
            }
        }

        private void InitializeProperties()
        {
            MaxHealth = _configuration.BaseHealth * GetDifficultyMultiplier();
            Difficulty = _configuration.Difficulty;
            Position = _configuration.SpawnPosition;
            Rotation = Quaternion.Identity;
        }

        private async Task InitializeInventoryAsync(CancellationToken cancellationToken)
        {
            // Varsayılan ekipmanları yükle;
            foreach (var item in _configuration.StartingEquipment)
            {
                await Inventory.AddItemAsync(item, cancellationToken);
            }
        }

        private async Task<List<EnemyAbility>> LoadAbilitiesAsync(CancellationToken cancellationToken)
        {
            var abilities = new List<EnemyAbility>();

            // Konfigürasyondan yetenekleri yükle;
            foreach (var abilityConfig in _configuration.Abilities)
            {
                var ability = new EnemyAbility(
                    abilityConfig.Id,
                    abilityConfig.Name,
                    abilityConfig.Type,
                    abilityConfig.Damage,
                    abilityConfig.Cooldown,
                    abilityConfig.Range,
                    abilityConfig.ResourceCost;
                );

                abilities.Add(ability);
            }

            return await Task.FromResult(abilities);
        }

        #endregion;

        #region Behavior Tree;

        private BehaviorTree CreateBehaviorTree()
        {
            var tree = new BehaviorTree(EnemyId);

            // Ana düşünce süreci;
            var root = new SequenceNode("Root");

            // Algılama ve değerlendirme;
            var perceptionSequence = new SequenceNode("Perception");
            perceptionSequence.AddChild(new ConditionNode("IsAlive", () => IsAlive));
            perceptionSequence.AddChild(new ActionNode("UpdatePerception", UpdatePerception));
            perceptionSequence.AddChild(new ActionNode("AssessThreats", AssessThreats));

            // Karar verme;
            var decisionSelector = new SelectorNode("DecisionMaking");

            // Savaş durumu;
            var combatSequence = new SequenceNode("Combat");
            combatSequence.AddChild(new ConditionNode("HasTarget", () => HasValidTarget()));
            combatSequence.AddChild(new ActionNode("ExecuteCombat", ExecuteCombatBehavior));

            // Takip durumu;
            var pursuitSequence = new SequenceNode("Pursuit");
            pursuitSequence.AddChild(new ConditionNode("ShouldPursue", ShouldPursue));
            pursuitSequence.AddChild(new ActionNode("PursueTarget", PursueTarget));

            // Keşif durumu;
            var patrolSequence = new SequenceNode("Patrol");
            patrolSequence.AddChild(new ConditionNode("ShouldPatrol", ShouldPatrol));
            patrolSequence.AddChild(new ActionNode("PatrolArea", PatrolArea));

            // Dinlenme durumu;
            var idleSequence = new SequenceNode("Idle");
            idleSequence.AddChild(new ActionNode("IdleBehavior", PerformIdleBehavior));

            decisionSelector.AddChild(combatSequence);
            decisionSelector.AddChild(pursuitSequence);
            decisionSelector.AddChild(patrolSequence);
            decisionSelector.AddChild(idleSequence);

            // Öğrenme ve adaptasyon;
            var learningSequence = new SequenceNode("Learning");
            learningSequence.AddChild(new ActionNode("UpdateMemory", UpdateMemory));
            learningSequence.AddChild(new ActionNode("AdaptBehavior", AdaptBehavior));

            root.AddChild(perceptionSequence);
            root.AddChild(decisionSelector);
            root.AddChild(learningSequence);

            tree.Root = root;
            return tree;
        }

        #endregion;

        #region Neural Network;

        private NeuralNetwork CreateNeuralNetwork()
        {
            var config = new NeuralNetworkConfiguration;
            {
                InputSize = 50,  // Algılanan özellikler;
                HiddenLayers = new[] { 128, 64, 32 },
                OutputSize = 10,  // Olası aksiyonlar;
                LearningRate = 0.001f,
                ActivationFunction = ActivationFunction.ReLU,
                UseDropout = true,
                DropoutRate = 0.2f;
            };

            return new NeuralNetwork(config);
        }

        private float[] GetNetworkInputs()
        {
            var inputs = new List<float>();

            // Sağlık durumu;
            inputs.Add(Health / MaxHealth);

            // Tehdit seviyeleri;
            inputs.Add(GetHighestThreatLevel());
            inputs.Add(ThreatAssessment.Count > 0 ? 1 : 0);

            // Mesafeler;
            inputs.Add(GetDistanceToNearestTarget());
            inputs.Add(GetAverageDistanceToThreats());

            // Kaynaklar;
            inputs.Add(GetResourceLevel("ammo"));
            inputs.Add(GetResourceLevel("mana"));
            inputs.Add(GetResourceLevel("stamina"));

            // Çevresel faktörler;
            inputs.Add(IsInCover() ? 1 : 0);
            inputs.Add(HasLineOfSight() ? 1 : 0);
            inputs.Add(GetTimeSinceLastCombat());

            // Zorluk faktörü;
            inputs.Add((float)Difficulty);

            // Eksikleri sıfırla;
            while (inputs.Count < 50)
            {
                inputs.Add(0);
            }

            return inputs.ToArray();
        }

        private async Task<CombatDecision> GetNeuralNetworkDecisionAsync()
        {
            try
            {
                var inputs = GetNetworkInputs();
                var outputs = await _neuralNetwork.PredictAsync(inputs);

                // Çıktıları yorumla;
                var decision = InterpretNetworkOutputs(outputs);
                return decision;
            }
            catch (Exception ex)
            {
                _logger.Error($"Neural network decision failed: {ex.Message}", ex);
                return CombatDecision.Default;
            }
        }

        private CombatDecision InterpretNetworkOutputs(float[] outputs)
        {
            // En yüksek değerli çıktıyı bul;
            int bestActionIndex = 0;
            float bestValue = outputs[0];

            for (int i = 1; i < outputs.Length; i++)
            {
                if (outputs[i] > bestValue)
                {
                    bestValue = outputs[i];
                    bestActionIndex = i;
                }
            }

            // İndex'i CombatDecision'a çevir;
            return (CombatDecision)bestActionIndex;
        }

        #endregion;

        #region Core AI Methods;

        /// <summary>
        /// Ana AI güncelleme döngüsü;
        /// </summary>
        private void StartAILoop(CancellationToken cancellationToken)
        {
            Task.Run(async () =>
            {
                while (!cancellationToken.IsCancellationRequested && _isInitialized)
                {
                    try
                    {
                        await UpdateAIAsync(cancellationToken);
                        await Task.Delay(_configuration.UpdateIntervalMs, cancellationToken);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                    catch (Exception ex)
                    {
                        _logger.Error($"AI loop error: {ex.Message}", ex);
                        await Task.Delay(1000, cancellationToken);
                    }
                }
            }, cancellationToken);
        }

        /// <summary>
        /// AI durumunu günceller;
        /// </summary>
        public async Task UpdateAIAsync(CancellationToken cancellationToken = default)
        {
            if (!_isInitialized || !IsAlive) return;

            lock (_stateLock)
            {
                _currentState.LastUpdateTime = DateTime.UtcNow;
            }

            // Behavior Tree'yi çalıştır;
            if (_behaviorTree != null)
            {
                await _behaviorTree.ExecuteAsync(cancellationToken);
            }

            // Neural Network kararı al;
            var nnDecision = await GetNeuralNetworkDecisionAsync();
            ProcessNeuralNetworkDecision(nnDecision);

            // Durum makinesini güncelle;
            UpdateStateMachine();

            // Performans optimizasyonu;
            CleanupOldData();
        }

        private void UpdatePerception()
        {
            // Görsel algılama;
            UpdateVisualPerception();

            // İşitsel algılama;
            UpdateAuditoryPerception();

            // Duyusal algılama;
            UpdateSensoryPerception();

            // Algılanan verileri blackboard'a kaydet;
            _blackboard.SetValue("perception_updated", DateTime.UtcNow.Ticks);
        }

        private void AssessThreats()
        {
            var threats = _threatDetector.DetectThreats(Position, Rotation);

            lock (_stateLock)
            {
                ThreatAssessment.Clear();
                foreach (var threat in threats)
                {
                    float threatLevel = CalculateThreatLevel(threat);
                    ThreatAssessment[threat.Id] = threatLevel;

                    // Blackboard'a kaydet;
                    _blackboard.SetValue($"threat_{threat.Id}", threatLevel);
                }
            }

            // En yüksek tehdidi belirle;
            UpdatePrimaryTarget();
        }

        private bool HasValidTarget()
        {
            return _blackboard.TryGetValue<string>("primary_target", out var targetId) &&
                   !string.IsNullOrEmpty(targetId) &&
                   ThreatAssessment.ContainsKey(targetId);
        }

        private void ExecuteCombatBehavior()
        {
            if (!_blackboard.TryGetValue<string>("primary_target", out var targetId))
                return;

            // Taktiksel karar ver;
            var tactic = _tacticalAI.DecideTactic(
                targetId,
                Position,
                ThreatAssessment[targetId],
                Health / MaxHealth;
            );

            // Kararı uygula;
            switch (tactic)
            {
                case CombatTactic.Aggressive:
                    ExecuteAggressiveAttack(targetId);
                    break;
                case CombatTactic.Defensive:
                    ExecuteDefensiveManeuver(targetId);
                    break;
                case CombatTactic.Flanking:
                    ExecuteFlankingMove(targetId);
                    break;
                case CombatTactic.Ranged:
                    ExecuteRangedAttack(targetId);
                    break;
                case CombatTactic.Stealth:
                    ExecuteStealthAttack(targetId);
                    break;
            }

            // Yetenek kullanımı;
            UseAppropriateAbility(tactic);
        }

        #endregion;

        #region Combat Actions;

        /// <summary>
        /// Saldırı gerçekleştir;
        /// </summary>
        public async Task<AttackResult> PerformAttackAsync(string targetId, EnemyAbility ability = null)
        {
            if (!IsAlive) return AttackResult.Failed("Enemy is dead");

            try
            {
                ability ??= SelectBestAbilityForTarget(targetId);

                if (ability == null)
                {
                    return AttackResult.Failed("No suitable ability available");
                }

                if (!ability.CanUse())
                {
                    return AttackResult.Failed("Ability is on cooldown");
                }

                // Hasar hesapla;
                float damage = CalculateDamage(ability, targetId);

                // Saldırıyı gerçekleştir;
                var result = await _combatSystem.ExecuteAttackAsync(
                    EnemyId,
                    targetId,
                    ability,
                    damage,
                    Position;
                );

                // Yetenek kullanımını kaydet;
                ability.MarkUsed();

                // Belleğe kaydet;
                Memory.RecordAttack(targetId, ability.Id, damage, result.Success);

                // Neural Network'i güncelle;
                await UpdateNeuralNetworkFromAttack(result);

                _logger.Info($"{EnemyId} attacked {targetId} with {ability.Name}: {damage} damage");

                return result;
            }
            catch (Exception ex)
            {
                _logger.Error($"Attack failed: {ex.Message}", ex);
                return AttackResult.Failed($"Attack failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Hasar al;
        /// </summary>
        public async Task<TakeDamageResult> TakeDamageAsync(float damage, string sourceId, DamageType damageType)
        {
            if (!IsAlive) return TakeDamageResult.AlreadyDead;

            lock (_stateLock)
            {
                float actualDamage = CalculateActualDamage(damage, damageType);
                Health -= actualDamage;

                if (Health <= 0)
                {
                    Health = 0;
                    _currentState = EnemyState.Dead;
                    OnDeath(sourceId);
                }

                // Tehdit seviyesini güncelle;
                if (ThreatAssessment.ContainsKey(sourceId))
                {
                    ThreatAssessment[sourceId] += actualDamage * 2; // Hasar almak tehdit algısını arttırır;
                }
            }

            // AI tepkisi;
            ReactToDamage(sourceId, damageType);

            // Belleğe kaydet;
            Memory.RecordDamage(sourceId, damage, damageType);

            // Neural Network'i güncelle;
            await UpdateNeuralNetworkFromDamage(damage, sourceId);

            var result = new TakeDamageResult;
            {
                DamageTaken = damage,
                CurrentHealth = Health,
                IsDead = !IsAlive;
            };

            return await Task.FromResult(result);
        }

        /// <summary>
        /// Hareket et;
        /// </summary>
        public async Task<MoveResult> MoveToAsync(Vector3 destination, MoveSpeed speed = MoveSpeed.Normal)
        {
            if (!IsAlive) return MoveResult.Failed("Enemy is dead");

            try
            {
                // Yolu hesapla;
                var path = await CalculatePathAsync(destination);

                if (path == null || path.Count == 0)
                {
                    return MoveResult.Failed("No valid path found");
                }

                // Hareketi gerçekleştir;
                await ExecuteMovementAsync(path, speed);

                // Pozisyonu güncelle;
                Position = destination;

                // Durumu güncelle;
                _currentState.LastMovementTime = DateTime.UtcNow;

                return MoveResult.Success(destination);
            }
            catch (Exception ex)
            {
                _logger.Error($"Movement failed: {ex.Message}", ex);
                return MoveResult.Failed($"Movement failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Yetenek kullan;
        /// </summary>
        public async Task<AbilityResult> UseAbilityAsync(string abilityId, string targetId = null)
        {
            var ability = Abilities.Find(a => a.Id == abilityId);
            if (ability == null)
            {
                return AbilityResult.Failed($"Ability {abilityId} not found");
            }

            if (!ability.CanUse())
            {
                return AbilityResult.Failed("Ability is on cooldown");
            }

            try
            {
                // Yeteneği kullan;
                var result = await ability.ExecuteAsync(this, targetId);

                if (result.Success)
                {
                    // Kaynak tüketimi;
                    ConsumeResources(ability.ResourceCost);

                    // Belleğe kaydet;
                    Memory.RecordAbilityUse(abilityId, targetId, result.Effectiveness);
                }

                return result;
            }
            catch (Exception ex)
            {
                _logger.Error($"Ability use failed: {ex.Message}", ex);
                return AbilityResult.Failed($"Ability use failed: {ex.Message}");
            }
        }

        #endregion;

        #region Tactical Methods;

        private void ExecuteAggressiveAttack(string targetId)
        {
            // Doğrudan saldırı;
            var targetPosition = GetTargetPosition(targetId);
            var direction = Vector3.Normalize(targetPosition - Position);

            // Yakın mesafe kontrolü;
            float distance = Vector3.Distance(Position, targetPosition);
            if (distance > _configuration.AttackRange)
            {
                // Yaklaş;
                MoveToAsync(targetPosition, MoveSpeed.Fast);
            }
            else;
            {
                // Saldır;
                var ability = SelectCloseCombatAbility();
                PerformAttackAsync(targetId, ability);
            }
        }

        private void ExecuteDefensiveManeuver(string targetId)
        {
            // Savunma pozisyonu al;
            var coverPosition = FindNearestCover();
            if (coverPosition.HasValue)
            {
                MoveToAsync(coverPosition.Value, MoveSpeed.Normal);
            }

            // Defansif yetenek kullan;
            var defensiveAbility = SelectDefensiveAbility();
            if (defensiveAbility != null)
            {
                UseAbilityAsync(defensiveAbility.Id);
            }
        }

        private void ExecuteFlankingMove(string targetId)
        {
            // Yan pozisyon bul;
            var flankPosition = CalculateFlankPosition(targetId);
            if (flankPosition.HasValue)
            {
                // Yan hareket;
                MoveToAsync(flankPosition.Value, MoveSpeed.Stealth);

                // Bekleme;
                Task.Delay(1000).ContinueWith(_ =>
                {
                    // Sürpriz saldırı;
                    var stealthAbility = SelectStealthAbility();
                    PerformAttackAsync(targetId, stealthAbility);
                });
            }
        }

        private void ExecuteRangedAttack(string targetId)
        {
            // Mesafeyi koru;
            var targetPosition = GetTargetPosition(targetId);
            float optimalDistance = GetOptimalRangeDistance();
            float currentDistance = Vector3.Distance(Position, targetPosition);

            if (currentDistance < optimalDistance)
            {
                // Geri çekil;
                var retreatPosition = CalculateRetreatPosition(targetPosition);
                MoveToAsync(retreatPosition, MoveSpeed.Normal);
            }

            // Uzun menzilli saldırı;
            var rangedAbility = SelectRangedAbility();
            if (rangedAbility != null)
            {
                PerformAttackAsync(targetId, rangedAbility);
            }
        }

        private void ExecuteStealthAttack(string targetId)
        {
            // Gizlenme;
            if (!IsStealthed())
            {
                var stealthAbility = Abilities.Find(a => a.Type == AbilityType.Stealth);
                if (stealthAbility != null)
                {
                    UseAbilityAsync(stealthAbility.Id);
                }
            }

            // Yaklaşma;
            var approachPosition = CalculateStealthApproachPosition(targetId);
            if (approachPosition.HasValue)
            {
                MoveToAsync(approachPosition.Value, MoveSpeed.Stealth);
            }

            // Sürpriz saldırı;
            Task.Delay(2000).ContinueWith(_ =>
            {
                var surpriseAbility = SelectSurpriseAttackAbility();
                PerformAttackAsync(targetId, surpriseAbility);
            });
        }

        #endregion;

        #region Utility Methods;

        private float CalculateThreatLevel(ThreatInfo threat)
        {
            float baseThreat = threat.BaseThreatLevel;
            float distanceMultiplier = 1.0f / Math.Max(1.0f, threat.Distance);
            float damageMultiplier = threat.RecentDamage * 2.0f;
            float visibilityMultiplier = threat.IsVisible ? 1.5f : 0.5f;

            return baseThreat * distanceMultiplier * damageMultiplier * visibilityMultiplier;
        }

        private float CalculateDamage(EnemyAbility ability, string targetId)
        {
            float baseDamage = ability.Damage;
            float difficultyMultiplier = GetDifficultyMultiplier();
            float targetResistance = GetTargetResistance(targetId, ability.Type);
            float criticalChance = _configuration.CriticalHitChance;

            // Kritik vuruş kontrolü;
            bool isCritical = new Random().NextDouble() < criticalChance;
            float criticalMultiplier = isCritical ? _configuration.CriticalDamageMultiplier : 1.0f;

            // Nihai hasar;
            float finalDamage = baseDamage * difficultyMultiplier * criticalMultiplier * (1.0f - targetResistance);

            return Math.Max(1.0f, finalDamage);
        }

        private float CalculateActualDamage(float incomingDamage, DamageType damageType)
        {
            float resistance = GetResistance(damageType);
            float armor = GetArmorValue();

            float damageAfterResistance = incomingDamage * (1.0f - resistance);
            float damageAfterArmor = damageAfterResistance * (1.0f - armor / 100.0f);

            return Math.Max(1.0f, damageAfterArmor);
        }

        private EnemyAbility SelectBestAbilityForTarget(string targetId)
        {
            // Hedefe göre en iyi yeteneği seç;
            var targetType = GetTargetType(targetId);
            float distance = GetDistanceToTarget(targetId);

            return Abilities;
                .Where(a => a.CanUse())
                .Where(a => a.Range >= distance)
                .OrderByDescending(a => GetAbilityEffectiveness(a, targetType, distance))
                .FirstOrDefault();
        }

        private float GetAbilityEffectiveness(EnemyAbility ability, TargetType targetType, float distance)
        {
            float effectiveness = ability.Damage;

            // Mesafe uygunluğu;
            if (distance > ability.Range * 0.8f)
            {
                effectiveness *= 0.7f;
            }

            // Hedef tipi uygunluğu;
            switch (targetType)
            {
                case TargetType.Heavy:
                    if (ability.Type == AbilityType.ArmorPiercing)
                        effectiveness *= 1.5f;
                    break;
                case TargetType.Light:
                    if (ability.Type == AbilityType.RapidFire)
                        effectiveness *= 1.3f;
                    break;
                case TargetType.Flying:
                    if (ability.Type == AbilityType.Ranged)
                        effectiveness *= 1.4f;
                    break;
            }

            return effectiveness;
        }

        private void UpdatePrimaryTarget()
        {
            if (ThreatAssessment.Count == 0)
            {
                _blackboard.RemoveValue("primary_target");
                return;
            }

            var primaryTarget = ThreatAssessment;
                .OrderByDescending(kv => kv.Value)
                .First();

            _blackboard.SetValue("primary_target", primaryTarget.Key);
            _blackboard.SetValue("primary_threat_level", primaryTarget.Value);
        }

        private void ReactToDamage(string sourceId, DamageType damageType)
        {
            // Hasar tepkisi;
            float painThreshold = MaxHealth * 0.3f;
            float damageTaken = Math.Min(Health, MaxHealth - Health); // Son hasar miktarı;

            if (damageTaken > painThreshold)
            {
                // Ağır hasar tepkisi;
                Aggression = AggressionLevel.Frenzied;
                _blackboard.SetValue("in_pain", true);

                // Geri çekilme veya karşı saldırı;
                if (Health < MaxHealth * 0.4f)
                {
                    ExecuteDefensiveManeuver(sourceId);
                }
                else;
                {
                    ExecuteAggressiveAttack(sourceId);
                }
            }
            else;
            {
                // Normal hasar tepkisi;
                Aggression = AggressionLevel.Aggressive;
            }

            // Hasar tipine özel tepkiler;
            switch (damageType)
            {
                case DamageType.Fire:
                    // Yangın tepkisi;
                    TryExtinguishFire();
                    break;
                case DamageType.Ice:
                    // Donma tepkisi;
                    BreakFreeFromIce();
                    break;
                case DamageType.Electric:
                    // Elektrik tepkisi;
                    GroundElectricity();
                    break;
            }
        }

        #endregion;

        #region State Management;

        private void UpdateStateMachine()
        {
            var oldState = _currentState;

            // Mevcut duruma göre yeni durumu belirle;
            if (!IsAlive)
            {
                _currentState = EnemyState.Dead;
            }
            else if (ThreatAssessment.Count > 0)
            {
                float highestThreat = ThreatAssessment.Values.Max();

                if (highestThreat > _configuration.HighThreatThreshold)
                {
                    _currentState = EnemyState.Combat;
                }
                else if (highestThreat > _configuration.LowThreatThreshold)
                {
                    _currentState = EnemyState.Alert;
                }
                else;
                {
                    _currentState = EnemyState.Patrol;
                }
            }
            else;
            {
                _currentState = EnemyState.Idle;
            }

            // Durum değişikliği olduysa event tetikle;
            if (oldState != _currentState)
            {
                OnStateChanged(oldState, _currentState);
            }
        }

        private void OnStateChanged(EnemyState oldState, EnemyState newState)
        {
            _logger.Info($"{EnemyId} state changed: {oldState} -> {newState}");

            // Durum değişikliği işlemleri;
            _blackboard.SetValue("previous_state", oldState.ToString());
            _blackboard.SetValue("current_state", newState.ToString());

            // Neural Network için veri topla;
            Memory.RecordStateChange(oldState, newState, ThreatAssessment);

            // UI güncellemesi;
            NotifyStateChange(newState);
        }

        private void OnDeath(string killerId)
        {
            _logger.Info($"{EnemyId} died. Killer: {killerId}");

            // Ölüm animasyonu ve efekti;
            PlayDeathAnimation();
            DropLoot();

            // Event tetikle;
            EnemyDied?.Invoke(this, new EnemyDeathEventArgs;
            {
                EnemyId = EnemyId,
                KillerId = killerId,
                Position = Position,
                LootDropped = GenerateLoot()
            });

            // AI döngüsünü durdur;
            _aiLoopTokenSource?.Cancel();
        }

        #endregion;

        #region Learning and Adaptation;

        private async Task UpdateNeuralNetworkFromAttack(AttackResult result)
        {
            try
            {
                // Saldırı sonucunu öğrenme verisi olarak kullan;
                var trainingData = new TrainingData;
                {
                    Inputs = GetNetworkInputs(),
                    ExpectedOutputs = CalculateExpectedOutputs(result),
                    Weight = result.Success ? 1.0f : 0.5f;
                };

                await _neuralNetwork.TrainAsync(new[] { trainingData }, 1);
            }
            catch (Exception ex)
            {
                _logger.Error($"Neural network training failed: {ex.Message}", ex);
            }
        }

        private async Task UpdateNeuralNetworkFromDamage(float damage, string sourceId)
        {
            try
            {
                // Hasar alma deneyimini öğren;
                var trainingData = new TrainingData;
                {
                    Inputs = GetNetworkInputs(),
                    ExpectedOutputs = CalculateDamageResponseOutputs(damage, sourceId),
                    Weight = damage / MaxHealth // Hasarın önemine göre ağırlık;
                };

                await _neuralNetwork.TrainAsync(new[] { trainingData }, 1);
            }
            catch (Exception ex)
            {
                _logger.Error($"Neural network damage training failed: {ex.Message}", ex);
            }
        }

        private void AdaptBehavior()
        {
            // Oynanışa göre davranış adaptasyonu;
            var playerBehavior = Memory.GetPlayerBehaviorPatterns();

            foreach (var pattern in playerBehavior)
            {
                if (pattern.Occurrences > 3) // Yeterli veri varsa;
                {
                    DevelopCounterStrategy(pattern);
                }
            }

            // Zorluk adaptasyonu;
            if (Memory.TotalPlayerDeaths > 2)
            {
                // Oyuncu zorlanıyorsa, zorluğu azalt;
                Difficulty = (EnemyDifficulty)Math.Max((int)Difficulty - 1, (int)EnemyDifficulty.VeryEasy);
            }
            else if (Memory.TotalPlayerKills > 5)
            {
                // Oyuncu iyi gidiyorsa, zorluğu arttır;
                Difficulty = (EnemyDifficulty)Math.Min((int)Difficulty + 1, (int)EnemyDifficulty.Impossible);
            }
        }

        private void DevelopCounterStrategy(PlayerBehaviorPattern pattern)
        {
            // Oyuncu davranışına karşı strateji geliştir;
            switch (pattern.BehaviorType)
            {
                case PlayerBehaviorType.AggressiveRush:
                    // Agresif oyuncuya karşı savunma;
                    _configuration.DefensiveTendency += 0.1f;
                    _configuration.AggressionThreshold += 0.2f;
                    break;

                case PlayerBehaviorType.Stealthy:
                    // Gizli oyuncuya karşı algılama;
                    _configuration.DetectionRange *= 1.2f;
                    _configuration.StealthDetection += 0.15f;
                    break;

                case PlayerBehaviorType.Ranged:
                    // Uzun menzilli oyuncuya karşı yakınlaşma;
                    _configuration.PreferredCombatRange *= 0.8f;
                    _configuration.MovementSpeed *= 1.1f;
                    break;

                case PlayerBehaviorType.Tactical:
                    // Taktiksel oyuncuya karşı tahmin edilemezlik;
                    _configuration.BehaviorRandomness += 0.1f;
                    _configuration.TacticChangeFrequency *= 1.3f;
                    break;
            }
        }

        #endregion;

        #region Save/Load;

        /// <summary>
        /// Düşman durumunu kaydeder;
        /// </summary>
        public EnemySaveState SaveState()
        {
            return new EnemySaveState;
            {
                EnemyId = EnemyId,
                EnemyType = EnemyType,
                Configuration = _configuration,
                Health = Health,
                Position = Position,
                Rotation = Rotation,
                CurrentState = _currentState,
                Aggression = Aggression,
                Difficulty = Difficulty,
                ThreatAssessment = new Dictionary<string, float>(ThreatAssessment),
                MemoryData = Memory.Save(),
                InventoryState = Inventory.SaveState(),
                BehaviorTreeState = _behaviorTree?.SaveState(),
                NeuralNetworkState = _neuralNetwork?.SaveState()
            };
        }

        /// <summary>
        /// Kaydedilmiş durumdan yükler;
        /// </summary>
        private void LoadFromSaveState(EnemySaveState saveState)
        {
            Health = saveState.Health;
            Position = saveState.Position;
            Rotation = saveState.Rotation;
            _currentState = saveState.CurrentState;
            Aggression = saveState.Aggression;
            Difficulty = saveState.Difficulty;
            ThreatAssessment = saveState.ThreatAssessment ?? new Dictionary<string, float>();

            // Belleği yükle;
            Memory = new EnemyMemory(_configuration.MemoryCapacity);
            Memory.Load(saveState.MemoryData);

            // Inventory yükle;
            Inventory = new Inventory(_configuration.InventorySize);
            Inventory.LoadState(saveState.InventoryState);

            // Behavior Tree yükle;
            if (saveState.BehaviorTreeState != null)
            {
                _behaviorTree = new BehaviorTree(EnemyId);
                _behaviorTree.LoadState(saveState.BehaviorTreeState);
            }

            // Neural Network yükle;
            if (saveState.NeuralNetworkState != null)
            {
                _neuralNetwork = CreateNeuralNetwork();
                _neuralNetwork.LoadState(saveState.NeuralNetworkState);
            }
        }

        #endregion;

        #region Events;

        /// <summary>
        /// Düşman öldüğünde tetiklenir;
        /// </summary>
        public event EventHandler<EnemyDeathEventArgs> EnemyDied;

        /// <summary>
        /// Düşman durumu değiştiğinde tetiklenir;
        /// </summary>
        public event EventHandler<EnemyStateChangedEventArgs> StateChanged;

        /// <summary>
        /// Düşman saldırdığında tetiklenir;
        /// </summary>
        public event EventHandler<EnemyAttackEventArgs> Attacked;

        /// <summary>
        /// Düşman hasar aldığında tetiklenir;
        /// </summary>
        public event EventHandler<EnemyDamageEventArgs> DamageTaken;

        private void NotifyStateChange(EnemyState newState)
        {
            StateChanged?.Invoke(this, new EnemyStateChangedEventArgs;
            {
                EnemyId = EnemyId,
                OldState = _currentState,
                NewState = newState,
                Position = Position,
                HealthPercentage = Health / MaxHealth;
            });
        }

        #endregion;

        #region Helper Methods;

        private float GetDifficultyMultiplier()
        {
            return Difficulty switch;
            {
                EnemyDifficulty.VeryEasy => 0.5f,
                EnemyDifficulty.Easy => 0.75f,
                EnemyDifficulty.Normal => 1.0f,
                EnemyDifficulty.Hard => 1.5f,
                EnemyDifficulty.VeryHard => 2.0f,
                EnemyDifficulty.Impossible => 3.0f,
                _ => 1.0f;
            };
        }

        private bool ShouldPursue()
        {
            if (!HasValidTarget()) return false;

            float threatLevel = GetHighestThreatLevel();
            float distance = GetDistanceToPrimaryTarget();

            return threatLevel > _configuration.PursuitThreshold &&
                   distance < _configuration.MaxPursuitRange;
        }

        private bool ShouldPatrol()
        {
            return ThreatAssessment.Count == 0 &&
                   _currentState != EnemyState.Combat &&
                   _currentState != EnemyState.Alert;
        }

        private void CleanupOldData()
        {
            // Eski tehdit verilerini temizle;
            var oldThreats = ThreatAssessment;
                .Where(kv => IsThreatOld(kv.Key))
                .Select(kv => kv.Key)
                .ToList();

            foreach (var oldThreat in oldThreats)
            {
                ThreatAssessment.Remove(oldThreat);
            }

            // Bellek optimizasyonu;
            Memory.CleanupOldMemories();
        }

        private bool IsThreatOld(string threatId)
        {
            // Tehdit zaman aşımı kontrolü;
            if (_blackboard.TryGetValue<long>($"threat_time_{threatId}", out var timestamp))
            {
                var timeSpan = DateTime.UtcNow - new DateTime(timestamp);
                return timeSpan.TotalSeconds > _configuration.ThreatTimeoutSeconds;
            }
            return true;
        }

        #endregion;

        #region IDisposable Implementation;

        private bool _disposed = false;

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (!_disposed)
            {
                if (disposing)
                {
                    // Yönetilen kaynakları serbest bırak;
                    _aiLoopTokenSource?.Cancel();
                    _aiLoopTokenSource?.Dispose();
                    _neuralNetwork?.Dispose();
                    _behaviorTree?.Dispose();
                }

                // Yönetilmeyen kaynakları serbest bırak;
                _disposed = true;
            }
        }

        ~EnemyController()
        {
            Dispose(false);
        }

        #endregion;
    }

    #region Supporting Types;

    /// <summary>
    /// Düşman durumları;
    /// </summary>
    public enum EnemyState;
    {
        Idle,
        Patrol,
        Alert,
        Combat,
        Dead,
        Fleeing,
        Searching;
    }

    /// <summary>
    /// Düşman tipi;
    /// </summary>
    public enum EnemyType;
    {
        Melee,
        Ranged,
        Magic,
        Boss,
        MiniBoss,
        Support,
        Tank,
        Assassin,
        Hybrid;
    }

    /// <summary>
    /// Zorluk seviyesi;
    /// </summary>
    public enum EnemyDifficulty;
    {
        VeryEasy,
        Easy,
        Normal,
        Hard,
        VeryHard,
        Impossible;
    }

    /// <summary>
    /// Saldırganlık seviyesi;
    /// </summary>
    public enum AggressionLevel;
    {
        Passive,
        Defensive,
        Neutral,
        Aggressive,
        Frenzied,
        Berserk;
    }

    /// <summary>
    /// Hasar tipi;
    /// </summary>
    public enum DamageType;
    {
        Physical,
        Fire,
        Ice,
        Electric,
        Poison,
        Magic,
        Pure;
    }

    /// <summary>
    /// Hareket hızı;
    /// </summary>
    public enum MoveSpeed;
    {
        Slow,
        Normal,
        Fast,
        Sprint,
        Stealth;
    }

    /// <summary>
    /// Taktik türü;
    /// </summary>
    public enum CombatTactic;
    {
        Aggressive,
        Defensive,
        Flanking,
        Ranged,
        Stealth,
        Support,
        Retreat;
    }

    /// <summary>
    /// Savaş kararı;
    /// </summary>
    public enum CombatDecision;
    {
        Attack,
        Defend,
        Flank,
        Retreat,
        UseAbility,
        Wait,
        Heal,
        CallReinforcements,
        UseEnvironment,
        Default;
    }

    /// <summary>
    /// Hedef tipi;
    /// </summary>
    public enum TargetType;
    {
        Player,
        Heavy,
        Light,
        Flying,
        Structure,
        Vehicle;
    }

    /// <summary>
    /// Düşman konfigürasyonu;
    /// </summary>
    public class EnemyConfiguration;
    {
        public float BaseHealth { get; set; } = 100f;
        public float BaseDamage { get; set; } = 10f;
        public float AttackRange { get; set; } = 5f;
        public float DetectionRange { get; set; } = 20f;
        public float MovementSpeed { get; set; } = 3f;
        public AggressionLevel BaseAggression { get; set; } = AggressionLevel.Neutral;
        public EnemyDifficulty Difficulty { get; set; } = EnemyDifficulty.Normal;
        public int InventorySize { get; set; } = 10;
        public float CriticalHitChance { get; set; } = 0.05f;
        public float CriticalDamageMultiplier { get; set; } = 2.0f;
        public float HighThreatThreshold { get; set; } = 0.7f;
        public float LowThreatThreshold { get; set; } = 0.3f;
        public float PursuitThreshold { get; set; } = 0.5f;
        public float MaxPursuitRange { get; set; } = 50f;
        public float ThreatTimeoutSeconds { get; set; } = 30f;
        public int UpdateIntervalMs { get; set; } = 100;
        public int MemoryCapacity { get; set; } = 1000;
        public Vector3 SpawnPosition { get; set; }
        public List<EnemyAbilityConfiguration> Abilities { get; set; } = new();
        public List<Item> StartingEquipment { get; set; } = new();

        // AI parametreleri;
        public float DefensiveTendency { get; set; } = 0.3f;
        public float AggressionThreshold { get; set; } = 0.6f;
        public float PreferredCombatRange { get; set; } = 8f;
        public float BehaviorRandomness { get; set; } = 0.1f;
        public float TacticChangeFrequency { get; set; } = 5f; // saniye;
        public float StealthDetection { get; set; } = 0.5f;
    }

    /// <summary>
    /// Saldırı sonucu;
    /// </summary>
    public class AttackResult;
    {
        public bool Success { get; set; }
        public float DamageDealt { get; set; }
        public bool WasCritical { get; set; }
        public string TargetId { get; set; }
        public string AbilityUsed { get; set; }
        public string FailureReason { get; set; }

        public static AttackResult Successful(float damage, string targetId, string ability, bool critical = false)
        {
            return new AttackResult;
            {
                Success = true,
                DamageDealt = damage,
                WasCritical = critical,
                TargetId = targetId,
                AbilityUsed = ability;
            };
        }

        public static AttackResult Failed(string reason)
        {
            return new AttackResult;
            {
                Success = false,
                FailureReason = reason;
            };
        }
    }

    /// <summary>
    /// Hasar alma sonucu;
    /// </summary>
    public class TakeDamageResult;
    {
        public float DamageTaken { get; set; }
        public float CurrentHealth { get; set; }
        public bool IsDead { get; set; }

        public static readonly TakeDamageResult AlreadyDead = new TakeDamageResult;
        {
            DamageTaken = 0,
            CurrentHealth = 0,
            IsDead = true;
        };
    }

    /// <summary>
    /// Hareket sonucu;
    /// </summary>
    public class MoveResult;
    {
        public bool Success { get; set; }
        public Vector3 NewPosition { get; set; }
        public float DistanceTraveled { get; set; }
        public string FailureReason { get; set; }

        public static MoveResult Success(Vector3 newPosition)
        {
            return new MoveResult;
            {
                Success = true,
                NewPosition = newPosition,
                DistanceTraveled = 0 // Önceki pozisyona ihtiyaç var;
            };
        }

        public static MoveResult Failed(string reason)
        {
            return new MoveResult;
            {
                Success = false,
                FailureReason = reason;
            };
        }
    }

    /// <summary>
    /// Yetenek sonucu;
    /// </summary>
    public class AbilityResult;
    {
        public bool Success { get; set; }
        public string AbilityId { get; set; }
        public float Effectiveness { get; set; }
        public Dictionary<string, object> AdditionalEffects { get; set; } = new();
        public string FailureReason { get; set; }

        public static AbilityResult Successful(string abilityId, float effectiveness = 1.0f)
        {
            return new AbilityResult;
            {
                Success = true,
                AbilityId = abilityId,
                Effectiveness = effectiveness;
            };
        }

        public static AbilityResult Failed(string reason)
        {
            return new AbilityResult;
            {
                Success = false,
                FailureReason = reason;
            };
        }
    }

    /// <summary>
    /// Düşman ölüm event args;
    /// </summary>
    public class EnemyDeathEventArgs : EventArgs;
    {
        public string EnemyId { get; set; }
        public string KillerId { get; set; }
        public Vector3 Position { get; set; }
        public List<Item> LootDropped { get; set; }
    }

    /// <summary>
    /// Düşman durum değişikliği event args;
    /// </summary>
    public class EnemyStateChangedEventArgs : EventArgs;
    {
        public string EnemyId { get; set; }
        public EnemyState OldState { get; set; }
        public EnemyState NewState { get; set; }
        public Vector3 Position { get; set; }
        public float HealthPercentage { get; set; }
    }

    /// <summary>
    /// Düşman saldırı event args;
    /// </summary>
    public class EnemyAttackEventArgs : EventArgs;
    {
        public string EnemyId { get; set; }
        public string TargetId { get; set; }
        public string AbilityId { get; set; }
        public float Damage { get; set; }
        public bool IsCritical { get; set; }
    }

    /// <summary>
    /// Düşman hasar event args;
    /// </summary>
    public class EnemyDamageEventArgs : EventArgs;
    {
        public string EnemyId { get; set; }
        public string SourceId { get; set; }
        public float Damage { get; set; }
        public DamageType DamageType { get; set; }
        public float RemainingHealth { get; set; }
    }

    /// <summary>
    /// Düşman kontrolü özel exception;
    /// </summary>
    public class EnemyControllerException : Exception
    {
        public EnemyControllerException(string message) : base(message) { }
        public EnemyControllerException(string message, Exception innerException) : base(message, innerException) { }
    }

    #endregion;
}
