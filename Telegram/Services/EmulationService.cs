using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Telegram.Common;
using Windows.Storage;

namespace Telegram.Services
{
    public interface IEmulationService
    {
        Task<EmulationPreset> GetPresetAsync(long id);
        Task<List<EmulationPreset>> GetPresetsAsync();
        Task AddPresetsAsync(EmulationPreset emulationPreset);
        Task EditPresetAsync(EmulationPreset emulationPreset);
        Task DeletePresetsAsync(params long[] ids);
    }

    public class EmulationPreset
    {
        public long Id { get; set; }
        public long LastUsedDate { get; set; }
        public string Title { get; set; }
        public string ApplicationName { get; set; }
        public string UserAgent { get; set; }
        public string XRequestedWith { get; set; }

        // Ch-Ua properties
        public string SecChUa { get; set; }
        public string SecChUaPlatform { get; set; }
        public bool? SecChUaMobile { get; set; }
        public string SecChUaArch { get; set; }
        public string SecChUaBitness { get; set; }
        public string SecChUaFormFactors { get; set; }
        public string SecChUaFullVersionList { get; set; }
        public string SecChUaModel { get; set; }
        public string SecChUaPlatformVersion { get; set; }
        public bool? SecChUaWoW64 { get; set; }

        // Sec-Ch-Prefers properties
        public string SecChPrefersColorScheme { get; set; }
        public bool? SecChPrefersReducedMotion { get; set; }
        public bool? SecChPrefersReducedTransparency { get; set; }

        // Sec-Fetch properties
        public bool? SecFetchUser { get; set; }
        public string SecFetchDest { get; set; }
        public string SecFetchMode { get; set; }
        public string SecFetchSite { get; set; }

        // WebSocket properties
        public string SecWebSocketAccept { get; set; }
        public string SecWebSocketExtensions { get; set; }
        public string SecWebSocketKey { get; set; }
        public string SecWebSocketProtocol { get; set; }
        public string SecWebSocketVersion { get; set; }

        // Other properties
        public string SecSpeculationTags { get; set; }
        public string SecPurpose { get; set; }
        public bool? SecGPC { get; set; }
    }

    public class EmulationService : IEmulationService
    {
        private readonly LocalDatabase _database;
        private static readonly string[] ColumnNames = {
            "Id", "LastUsedDate", "Title", "ApplicationName", "UserAgent", "SecChUa", "SecChUaPlatform",
            "SecChUaMobile", "XRequestedWith", "SecChPrefersColorScheme",
            "SecChPrefersReducedMotion", "SecChPrefersReducedTransparency",
            "SecChUaArch", "SecChUaBitness", "SecChUaFormFactors",
            "SecChUaFullVersionList", "SecChUaModel", "SecChUaPlatformVersion",
            "SecChUaWoW64", "SecFetchDest", "SecFetchMode", "SecFetchSite",
            "SecFetchUser", "SecGPC", "SecPurpose", "SecSpeculationTags",
            "SecWebSocketAccept", "SecWebSocketExtensions", "SecWebSocketKey",
            "SecWebSocketProtocol", "SecWebSocketVersion"
        };

        private static readonly string[] ColumnTypes = {
            "INTEGER PRIMARY KEY AUTOINCREMENT", "INTEGER", "TEXT NOT NULL", "TEXT NOT NULL",
            "TEXT", "TEXT", "TEXT", "INTEGER", "TEXT", "TEXT", "INTEGER",
            "INTEGER", "TEXT", "TEXT", "TEXT", "TEXT", "TEXT", "TEXT", "INTEGER",
            "TEXT", "TEXT", "TEXT", "INTEGER", "INTEGER", "TEXT", "TEXT", "TEXT", "TEXT",
            "TEXT", "TEXT", "TEXT"
        };

        public EmulationService()
        {
            _database = new LocalDatabase();
            _database.Initialize(Path.Combine(ApplicationData.Current.LocalFolder.Path, "local.db"));
            _database.CreateTable("EmulationPresets", ColumnNames, ColumnTypes);
        }

        public static IReadOnlyList<EmulationPreset> DefaultEmulationPresets = new[] {
            new EmulationPreset
            {
                Id = -1,
                Title = "Telegram Desktop",
                ApplicationName = "tdesktop",
                SecChUaPlatform = "\"Windows\"",
            },
            new EmulationPreset
            {
                Id = -2,
                Title = "Telegram Android",
                ApplicationName = "android",
                UserAgent = "Mozilla/5.0 (Linux; Android 16; K) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/137.0.7151.115 Mobile Safari/537.36 Telegram-Android/11.13.1 (Google Pixel 7; Android 16; SDK 36; HIGH)",
                XRequestedWith = "org.telegram.messenger",

                SecChUa = "\"Android WebView\";v=\"137\", \"Chromium\";v=\"137\", \"Not/A)Brand\";v=\"24\"",
                SecChUaPlatform = "\"Android\"",
                SecChUaMobile = true,
                SecChUaArch = "arm",
                SecChUaBitness = "64",
                SecChUaFormFactors = "\"Mobile\"",
                SecChUaFullVersionList = "\"Android WebView\";v=\"137.0.7151.115\", \"Chromium\";v=\"137.0.7151.115\", \"Not/A)Brand\";v=\"24.0.0.0\"",
                SecChUaModel = "\"Pixel 7\"",
                SecChUaPlatformVersion = "\"16\"",
                SecChUaWoW64 = false,
            }
        };

        public async Task<EmulationPreset> GetPresetAsync(long id)
        {
            return await Task.Run(() =>
            {
                if (id < 0)
                {
                    return DefaultEmulationPresets.First(x => x.Id == id);
                }
                var rows = _database.Select("EmulationPresets", "Id", id);
                if (rows.Count == 0)
                {
                    return null;
                }
                return CreatePresetFromRow(rows[0]);
            });
        }

        public async Task<List<EmulationPreset>> GetPresetsAsync()
        {
            return await Task.Run(() =>
            {
                var rows = _database.Select("EmulationPresets", null, new[] { "LastUsedDate" });
                return rows.Select(CreatePresetFromRow).ToList();
            });
        }

        public async Task AddPresetsAsync(EmulationPreset preset)
        {
            List<EmulationPreset> presets = await GetPresetsAsync();
            if (preset == null || presets.Any(x => x.Title == preset.Title))
            {
                return;
            }
            preset.LastUsedDate = DateTime.Now.ToTimestamp();
            var values = GetPresetValues(preset);
            values[0] = null; // Set "Id" to null for auto-increment
            _database.Insert("EmulationPresets", ColumnNames, new List<object[]> { values });
        }

        public Task DeletePresetsAsync(params long[] ids)
        {
            _database.Delete("EmulationPresets", "Id", ids.Cast<object>().ToArray());
            return Task.CompletedTask;
        }

        public Task EditPresetAsync(EmulationPreset preset)
        {
            preset.LastUsedDate = DateTime.Now.ToTimestamp();
            var values = GetPresetValues(preset);
            var updates = ColumnNames.Skip(1).Zip(values.Skip(1), (col, val) => new { col, val })
                .ToDictionary(x => x.col, x => x.val);
            _database.Update("EmulationPresets", updates, "Id = ?", preset.Id);
            return Task.CompletedTask;
        }

        private object[] GetPresetValues(EmulationPreset preset)
        {
            return new []
            {
                preset.Id, preset.LastUsedDate,
                preset.Title, preset.ApplicationName,
                preset.UserAgent, preset.SecChUa, preset.SecChUaPlatform,
                ConvertNullableBool(preset.SecChUaMobile),
                preset.XRequestedWith,
                preset.SecChPrefersColorScheme,
                ConvertNullableBool(preset.SecChPrefersReducedMotion),
                ConvertNullableBool(preset.SecChPrefersReducedTransparency),
                preset.SecChUaArch, preset.SecChUaBitness,
                preset.SecChUaFormFactors,
                preset.SecChUaFullVersionList, preset.SecChUaModel,
                preset.SecChUaPlatformVersion,
                ConvertNullableBool(preset.SecChUaWoW64),
                preset.SecFetchDest, preset.SecFetchMode, preset.SecFetchSite,
                ConvertNullableBool(preset.SecFetchUser),
                ConvertNullableBool(preset.SecGPC),
                preset.SecPurpose, preset.SecSpeculationTags,
                preset.SecWebSocketAccept, preset.SecWebSocketExtensions,
                preset.SecWebSocketKey, preset.SecWebSocketProtocol, preset.SecWebSocketVersion,
            };
        }

        private EmulationPreset CreatePresetFromRow(object[] row)
        {
            return new EmulationPreset
            {
                Id = (long)row[0],
                LastUsedDate = (long)row[1],
                Title = (string)row[2],
                ApplicationName = (string)row[3],
                UserAgent = (string)row[4],
                SecChUa = (string)row[5],
                SecChUaPlatform = (string)row[6],
                SecChUaMobile = ConvertToNullableBool(row[7]),
                XRequestedWith = (string)row[8],
                SecChPrefersColorScheme = (string)row[9],
                SecChPrefersReducedMotion = ConvertToNullableBool(row[10]),
                SecChPrefersReducedTransparency = ConvertToNullableBool(row[11]),
                SecChUaArch = (string)row[12],
                SecChUaBitness = (string)row[13],
                SecChUaFormFactors = (string)row[14],
                SecChUaFullVersionList = (string)row[15],
                SecChUaModel = (string)row[16],
                SecChUaPlatformVersion = (string)row[17],
                SecChUaWoW64 = ConvertToNullableBool(row[18]),
                SecFetchDest = (string)row[19],
                SecFetchMode = (string)row[20],
                SecFetchSite = (string)row[21],
                SecFetchUser = ConvertToNullableBool(row[22]),
                SecGPC = ConvertToNullableBool(row[23]),
                SecPurpose = (string)row[24],
                SecSpeculationTags = (string)row[25],
                SecWebSocketAccept = (string)row[26],
                SecWebSocketExtensions = (string)row[27],
                SecWebSocketKey = (string)row[28],
                SecWebSocketProtocol = (string)row[29],
                SecWebSocketVersion = (string)row[30],
            };
        }

        private static object ConvertNullableBool(bool? value) =>
            value.HasValue ? (value.Value ? 1 : 0) : null;

        private static bool? ConvertToNullableBool(object value) =>
            value == null ? null : Convert.ToBoolean(value);
    }
}