// Dependency Injection ile kullanım;
using NEDA.NeuralNetwork.PatternRecognition.BehavioralPatterns;
using System;

services.AddSingleton<ICacheManager<string, UserProfile>>(provider =>
{
    var logger = provider.GetRequiredService<ILogger<CacheManager<string, UserProfile>>>();
    var metrics = provider.GetRequiredService<IMetricsCollector>();

    var config = new CacheManager<string, UserProfile>.CacheConfiguration;
    {
        MaxCapacity = 5000,
        DefaultExpiration = TimeSpan.FromHours(1),
        EvictionPolicy = CacheManager<string, UserProfile>.CacheEvictionPolicy.LRU;
    };

    return new CacheManager<string, UserProfile>(logger, metrics, config);
});

// Önbellek kullanımı;
var cache = serviceProvider.GetService<ICacheManager<string, UserProfile>>();

// Öğe ekleme;
cache.Set(userId, userProfile, TimeSpan.FromMinutes(30),
          CacheManager<string, UserProfile>.CachePriority.High);

// Öğe alma;
var result = await cache.GetAsync(userId);
if (result.IsHit)
{
    var user = result.Value;
}

// GetOrCreate pattern;
var user = await cache.GetOrCreateAsync(userId, async () =>
{
    return await userRepository.GetUserAsync(userId);
});

// İstatistikler;
var stats = cache.GetStatistics();
var health = cache.GetHealthStatus();
