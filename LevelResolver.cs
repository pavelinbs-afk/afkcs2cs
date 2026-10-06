using System.Collections;
using System.Globalization;
using System.Reflection;
using System.Text.Json;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Capabilities;
using Microsoft.Extensions.Logging;

namespace AFK;

/// <summary>
/// Уровень игрока: LevelsRanks (capability <c>levels_ranks</c> / OnlineUsers.Rank),
/// иначе JSON-кэш steam64→level, иначе default=1.
/// </summary>
public sealed class LevelResolver
{
    public const int DefaultLevel = 1;
    private const string LevelsRanksCapabilityName = "levels_ranks";

    private readonly ILogger _logger;
    private readonly string _cacheFilePath;
    private readonly Dictionary<ulong, int> _jsonCache = new();
    private readonly object _cacheLock = new();

    private object? _lrApi;
    private MethodInfo? _convertToSteamId;
    private PropertyInfo? _onlineUsersProp;

    public LevelResolver(ILogger logger, string moduleDirectory)
    {
        _logger = logger;
        _cacheFilePath = Path.Combine(moduleDirectory, "levels_cache.json");
        LoadJsonCache();
    }

    public void TryBindLevelsRanks()
    {
        if (_lrApi != null)
        {
            return;
        }

        try
        {
            Type? apiType = FindType("LevelsRanksApi.ILevelsRanksApi")
                            ?? FindTypeByName("ILevelsRanksApi");
            if (apiType == null)
            {
                // Reflect без интерфейса: ищем любой объект с OnlineUsers через capability-обёртку.
                TryBindViaUntypedCapability();
                return;
            }

            Type capType = typeof(PluginCapability<>).MakeGenericType(apiType);
            object cap = Activator.CreateInstance(capType, LevelsRanksCapabilityName)!;
            MethodInfo? getMethod = capType.GetMethod("Get", BindingFlags.Instance | BindingFlags.Public);
            object? api = getMethod?.Invoke(cap, null);
            if (api == null)
            {
                _logger.LogDebug("[AFK] LevelsRanks capability '{Name}' not available yet.", LevelsRanksCapabilityName);
                return;
            }

            BindApi(api);
            _logger.LogInformation("[AFK] LevelsRanks API bound (capability '{Name}').", LevelsRanksCapabilityName);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[AFK] LevelsRanks bind failed; using cache/default level.");
        }
    }

    public int GetLevel(CCSPlayerController? player)
    {
        if (player == null || !player.IsValid || player.IsBot)
        {
            return DefaultLevel;
        }

        ulong sid64 = player.AuthorizedSteamID?.SteamId64 ?? 0UL;
        if (sid64 == 0UL)
        {
            return DefaultLevel;
        }

        return GetLevel(sid64);
    }

    public int GetLevel(ulong steamId64)
    {
        if (steamId64 == 0UL)
        {
            return DefaultLevel;
        }

        // Повторные попытки — LR может загрузиться позже.
        if (_lrApi == null)
        {
            TryBindLevelsRanks();
        }

        int? fromLr = TryGetRankFromLevelsRanks(steamId64);
        if (fromLr is > 0)
        {
            SetCachedLevel(steamId64, fromLr.Value, persist: false);
            return fromLr.Value;
        }

        lock (_cacheLock)
        {
            if (_jsonCache.TryGetValue(steamId64, out int cached) && cached > 0)
            {
                return cached;
            }
        }

        return DefaultLevel;
    }

    /// <summary>Опциональный мост: обновить кэш уровня (например из другого плагина).</summary>
    public void SetCachedLevel(ulong steamId64, int level, bool persist = true)
    {
        if (steamId64 == 0UL || level < 1)
        {
            return;
        }

        lock (_cacheLock)
        {
            _jsonCache[steamId64] = level;
        }

        if (persist)
        {
            SaveJsonCache();
        }
    }

    private int? TryGetRankFromLevelsRanks(ulong steamId64)
    {
        if (_lrApi == null || _onlineUsersProp == null)
        {
            return null;
        }

        try
        {
            object? onlineUsers = _onlineUsersProp.GetValue(_lrApi);
            if (onlineUsers == null)
            {
                return null;
            }

            string? steamId2 = null;
            if (_convertToSteamId != null)
            {
                steamId2 = _convertToSteamId.Invoke(_lrApi, [steamId64]) as string;
            }

            object? user = TryGetUser(onlineUsers, steamId2, steamId64.ToString(CultureInfo.InvariantCulture));
            if (user == null)
            {
                return null;
            }

            PropertyInfo? rankProp = user.GetType().GetProperty("Rank", BindingFlags.Instance | BindingFlags.Public);
            if (rankProp == null)
            {
                return null;
            }

            object? rankObj = rankProp.GetValue(user);
            if (rankObj == null)
            {
                return null;
            }

            int rank = Convert.ToInt32(rankObj, CultureInfo.InvariantCulture);
            return rank > 0 ? rank : null;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "[AFK] LR rank read failed for {Sid}", steamId64);
            return null;
        }
    }

    private static object? TryGetUser(object onlineUsers, string? steamId2, string steamId64)
    {
        // ConcurrentDictionary<string, User> — TryGetValue через reflection.
        MethodInfo? tryGet = onlineUsers.GetType().GetMethod("TryGetValue");
        if (tryGet != null)
        {
            foreach (string? key in new[] { steamId2, steamId64 })
            {
                if (string.IsNullOrEmpty(key))
                {
                    continue;
                }

                object?[] args = [key, null!];
                bool ok = (bool)(tryGet.Invoke(onlineUsers, args) ?? false);
                if (ok && args[1] != null)
                {
                    return args[1];
                }
            }
        }

        if (onlineUsers is IEnumerable enumerable)
        {
            foreach (object? entry in enumerable)
            {
                if (entry == null)
                {
                    continue;
                }

                Type et = entry.GetType();
                PropertyInfo? keyProp = et.GetProperty("Key");
                PropertyInfo? valProp = et.GetProperty("Value");
                if (keyProp == null || valProp == null)
                {
                    continue;
                }

                string? key = keyProp.GetValue(entry)?.ToString();
                if (key == null)
                {
                    continue;
                }

                if ((!string.IsNullOrEmpty(steamId2) &&
                     string.Equals(key, steamId2, StringComparison.OrdinalIgnoreCase)) ||
                    string.Equals(key, steamId64, StringComparison.OrdinalIgnoreCase))
                {
                    return valProp.GetValue(entry);
                }
            }
        }

        return null;
    }

    private void TryBindViaUntypedCapability()
    {
        // Fallback: сканируем сборки на тип LevelsRanksApi с OnlineUsers.
        foreach (Assembly asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            Type? impl = null;
            try
            {
                impl = asm.GetTypes().FirstOrDefault(t =>
                    t is { IsClass: true, IsAbstract: false } &&
                    t.GetProperty("OnlineUsers", BindingFlags.Instance | BindingFlags.Public) != null &&
                    (t.Name.Contains("LevelsRanks", StringComparison.OrdinalIgnoreCase) ||
                     t.GetInterfaces().Any(i => i.Name == "ILevelsRanksApi")));
            }
            catch
            {
                continue;
            }

            if (impl == null)
            {
                continue;
            }

            // Capability всё ещё предпочтительнее — но без generic-типа берём через object.
            try
            {
                Type capType = typeof(PluginCapability<>).MakeGenericType(impl.GetInterfaces()
                    .FirstOrDefault(i => i.Name == "ILevelsRanksApi") ?? impl);
                object cap = Activator.CreateInstance(capType, LevelsRanksCapabilityName)!;
                MethodInfo? getMethod = capType.GetMethod("Get", BindingFlags.Instance | BindingFlags.Public);
                object? api = getMethod?.Invoke(cap, null);
                if (api != null)
                {
                    BindApi(api);
                    _logger.LogInformation("[AFK] LevelsRanks API bound via untyped capability.");
                    return;
                }
            }
            catch
            {
                // continue
            }
        }
    }

    private void BindApi(object api)
    {
        _lrApi = api;
        Type t = api.GetType();
        _onlineUsersProp = t.GetProperty("OnlineUsers", BindingFlags.Instance | BindingFlags.Public);
        _convertToSteamId = t.GetMethod("ConvertToSteamId", BindingFlags.Instance | BindingFlags.Public, [typeof(ulong)]);
    }

    private static Type? FindType(string fullName)
    {
        foreach (Assembly asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            Type? t = asm.GetType(fullName, throwOnError: false);
            if (t != null)
            {
                return t;
            }
        }

        return null;
    }

    private static Type? FindTypeByName(string shortName)
    {
        foreach (Assembly asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            try
            {
                Type? t = asm.GetTypes().FirstOrDefault(x => x.Name == shortName);
                if (t != null)
                {
                    return t;
                }
            }
            catch
            {
                // ignore dynamic/reflection-only
            }
        }

        return null;
    }

    private void LoadJsonCache()
    {
        try
        {
            if (!File.Exists(_cacheFilePath))
            {
                return;
            }

            string json = File.ReadAllText(_cacheFilePath);
            Dictionary<string, int>? map = JsonSerializer.Deserialize<Dictionary<string, int>>(json);
            if (map == null)
            {
                return;
            }

            lock (_cacheLock)
            {
                _jsonCache.Clear();
                foreach (var kv in map)
                {
                    if (ulong.TryParse(kv.Key, out ulong sid) && kv.Value > 0)
                    {
                        _jsonCache[sid] = kv.Value;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[AFK] Failed to load levels_cache.json");
        }
    }

    private void SaveJsonCache()
    {
        try
        {
            Dictionary<string, int> map;
            lock (_cacheLock)
            {
                map = _jsonCache.ToDictionary(
                    kv => kv.Key.ToString(CultureInfo.InvariantCulture),
                    kv => kv.Value);
            }

            string? dir = Path.GetDirectoryName(_cacheFilePath);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            File.WriteAllText(
                _cacheFilePath,
                JsonSerializer.Serialize(map, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[AFK] Failed to save levels_cache.json");
        }
    }
}
