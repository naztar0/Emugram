//
// Copyright Fela Ameghino 2015-2025
//
// Distributed under the GNU General Public License v3.0. (See accompanying
// file LICENSE or copy at https://www.gnu.org/licenses/gpl-3.0.txt)
//
using System.Threading.Tasks;
using Telegram.Common;
using Telegram.Navigation;
using Telegram.Navigation.Services;
using Telegram.Services;
using Windows.UI.Xaml.Navigation;

namespace Telegram.ViewModels.Settings
{
    public class SettingsEmulationPresetViewModel : ViewModelBase
    {
        private readonly IEmulationService _emulationService;

        public SettingsEmulationPresetViewModel(IClientService clientService, ISettingsService settingsService, IEventAggregator aggregator, IEmulationService emulationService)
            : base(clientService, settingsService, aggregator)
        {
            _emulationService = emulationService;

            SaveCommand = new RelayCommand(Save);
        }

        #region Fields

        private long _id;
        public long Id
        {
            get => _id;
            set => Set(ref _id, value);
        }

        private string _title;
        public string Title
        {
            get => _title;
            set => Set(ref _title, value);
        }

        private string _applicationName;
        public string ApplicationName
        {
            get => _applicationName;
            set => Set(ref _applicationName, value);
        }

        private string _userAgent;
        public string UserAgent
        {
            get => _userAgent;
            set => Set(ref _userAgent, value);
        }

        private string _xRequestedWith;
        public string XRequestedWith
        {
            get => _xRequestedWith;
            set => Set(ref _xRequestedWith, value);
        }

        // Ch-Ua properties
        private string _secChUa;
        public string SecChUa
        {
            get => _secChUa;
            set => Set(ref _secChUa, value);
        }

        private string _secChUaPlatform;
        public string SecChUaPlatform
        {
            get => _secChUaPlatform;
            set => Set(ref _secChUaPlatform, value);
        }

        private bool? _secChUaMobile;
        public bool? SecChUaMobile
        {
            get => _secChUaMobile;
            set => Set(ref _secChUaMobile, value);
        }

        private string _secChUaArch;
        public string SecChUaArch
        {
            get => _secChUaArch;
            set => Set(ref _secChUaArch, value);
        }

        private string _secChUaBitness;
        public string SecChUaBitness
        {
            get => _secChUaBitness;
            set => Set(ref _secChUaBitness, value);
        }

        private string _secChUaFormFactors;
        public string SecChUaFormFactors
        {
            get => _secChUaFormFactors;
            set => Set(ref _secChUaFormFactors, value);
        }

        private string _secChUaFullVersionList;
        public string SecChUaFullVersionList
        {
            get => _secChUaFullVersionList;
            set => Set(ref _secChUaFullVersionList, value);
        }

        private string _secChUaModel;
        public string SecChUaModel
        {
            get => _secChUaModel;
            set => Set(ref _secChUaModel, value);
        }

        private string _secChUaPlatformVersion;
        public string SecChUaPlatformVersion
        {
            get => _secChUaPlatformVersion;
            set => Set(ref _secChUaPlatformVersion, value);
        }

        private bool? _secChUaWoW64;
        public bool? SecChUaWoW64
        {
            get => _secChUaWoW64;
            set => Set(ref _secChUaWoW64, value);
        }

        // Sec-Ch-Prefers properties
        private string _secChPrefersColorScheme;
        public string SecChPrefersColorScheme
        {
            get => _secChPrefersColorScheme;
            set => Set(ref _secChPrefersColorScheme, value);
        }

        private bool? _secChPrefersReducedMotion;
        public bool? SecChPrefersReducedMotion
        {
            get => _secChPrefersReducedMotion;
            set => Set(ref _secChPrefersReducedMotion, value);
        }

        private bool? _secChPrefersReducedTransparency;
        public bool? SecChPrefersReducedTransparency
        {
            get => _secChPrefersReducedTransparency;
            set => Set(ref _secChPrefersReducedTransparency, value);
        }

        // Sec-Fetch properties
        private bool? _secFetchUser;
        public bool? SecFetchUser
        {
            get => _secFetchUser;
            set => Set(ref _secFetchUser, value);
        }

        private string _secFetchDest;
        public string SecFetchDest
        {
            get => _secFetchDest;
            set => Set(ref _secFetchDest, value);
        }

        private string _secFetchMode;
        public string SecFetchMode
        {
            get => _secFetchMode;
            set => Set(ref _secFetchMode, value);
        }

        private string _secFetchSite;
        public string SecFetchSite
        {
            get => _secFetchSite;
            set => Set(ref _secFetchSite, value);
        }

        // WebSocket properties
        private string _secWebSocketAccept;
        public string SecWebSocketAccept
        {
            get => _secWebSocketAccept;
            set => Set(ref _secWebSocketAccept, value);
        }

        private string _secWebSocketExtensions;
        public string SecWebSocketExtensions
        {
            get => _secWebSocketExtensions;
            set => Set(ref _secWebSocketExtensions, value);
        }

        private string _secWebSocketKey;
        public string SecWebSocketKey
        {
            get => _secWebSocketKey;
            set => Set(ref _secWebSocketKey, value);
        }

        private string _secWebSocketProtocol;
        public string SecWebSocketProtocol
        {
            get => _secWebSocketProtocol;
            set => Set(ref _secWebSocketProtocol, value);
        }

        private string _secWebSocketVersion;
        public string SecWebSocketVersion
        {
            get => _secWebSocketVersion;
            set => Set(ref _secWebSocketVersion, value);
        }

        // Other properties
        private string _secSpeculationTags;
        public string SecSpeculationTags
        {
            get => _secSpeculationTags;
            set => Set(ref _secSpeculationTags, value);
        }

        private string _secPurpose;
        public string SecPurpose
        {
            get => _secPurpose;
            set => Set(ref _secPurpose, value);
        }

        private bool? _secGPC;
        public bool? SecGPC
        {
            get => _secGPC;
            set => Set(ref _secGPC, value);
        }

        #endregion

        protected override Task OnNavigatedToAsync(object parameter, NavigationMode mode, NavigationState state)
        {
            return Task.CompletedTask;
        }

        public async void LoadPreset(long id, bool duplicate)
        {
            var preset = await _emulationService.GetPresetAsync(id);
            if (preset == null)
            {
                return;
            }
            Id = duplicate ? 0 : preset.Id;
            Title = preset.Title;
            ApplicationName = preset.ApplicationName;
            UserAgent = preset.UserAgent;
            XRequestedWith = preset.XRequestedWith;

            SecChUa = preset.SecChUa;
            SecChUaPlatform = preset.SecChUaPlatform;
            SecChUaMobile = preset.SecChUaMobile;
            SecChUaArch = preset.SecChUaArch;
            SecChUaBitness = preset.SecChUaBitness;
            SecChUaFormFactors = preset.SecChUaFormFactors;
            SecChUaFullVersionList = preset.SecChUaFullVersionList;
            SecChUaModel = preset.SecChUaModel;
            SecChUaPlatformVersion = preset.SecChUaPlatformVersion;
            SecChUaWoW64 = preset.SecChUaWoW64;

            SecChPrefersColorScheme = preset.SecChPrefersColorScheme;
            SecChPrefersReducedMotion = preset.SecChPrefersReducedMotion;
            SecChPrefersReducedTransparency = preset.SecChPrefersReducedTransparency;

            SecFetchUser = preset.SecFetchUser;
            SecFetchDest = preset.SecFetchDest;
            SecFetchMode = preset.SecFetchMode;
            SecFetchSite = preset.SecFetchSite;

            SecWebSocketAccept = preset.SecWebSocketAccept;
            SecWebSocketExtensions = preset.SecWebSocketExtensions;
            SecWebSocketKey = preset.SecWebSocketKey;
            SecWebSocketProtocol = preset.SecWebSocketProtocol;
            SecWebSocketVersion = preset.SecWebSocketVersion;

            SecSpeculationTags = preset.SecSpeculationTags;
            SecPurpose = preset.SecPurpose;
            SecGPC = preset.SecGPC;
        }

        public RelayCommand SaveCommand { get; }
        public async void Save()
        {
            if (string.IsNullOrWhiteSpace(_title) || string.IsNullOrWhiteSpace(_applicationName))
            {
                return;
            }
            var emulationPreset = new EmulationPreset
            {
                Id = _id,
                Title = _title,
                ApplicationName = _applicationName,
                UserAgent = _userAgent,
                XRequestedWith = _xRequestedWith,

                // Ch-Ua properties
                SecChUa = _secChUa,
                SecChUaPlatform = _secChUaPlatform,
                SecChUaMobile = _secChUaMobile,
                SecChUaArch = _secChUaArch,
                SecChUaBitness = _secChUaBitness,
                SecChUaFormFactors = _secChUaFormFactors,
                SecChUaFullVersionList = _secChUaFullVersionList,
                SecChUaModel = _secChUaModel,
                SecChUaPlatformVersion = _secChUaPlatformVersion,
                SecChUaWoW64 = _secChUaWoW64,

                // Sec-Ch-Prefers properties
                SecChPrefersColorScheme = _secChPrefersColorScheme,
                SecChPrefersReducedMotion = _secChPrefersReducedMotion,
                SecChPrefersReducedTransparency = _secChPrefersReducedTransparency,

                // Sec-Fetch properties
                SecFetchUser = _secFetchUser,
                SecFetchDest = _secFetchDest,
                SecFetchMode = _secFetchMode,
                SecFetchSite = _secFetchSite,

                // WebSocket properties
                SecWebSocketAccept = _secWebSocketAccept,
                SecWebSocketExtensions = _secWebSocketExtensions,
                SecWebSocketKey = _secWebSocketKey,
                SecWebSocketProtocol = _secWebSocketProtocol,
                SecWebSocketVersion = _secWebSocketVersion,

                // Other properties
                SecSpeculationTags = _secSpeculationTags,
                SecPurpose = _secPurpose,
                SecGPC = _secGPC,
            };

            if (Id == 0)
            {
                await _emulationService.AddPresetsAsync(emulationPreset);
            } 
            else
            {
                await _emulationService.EditPresetAsync(emulationPreset);
            }

            NavigationService.GoBack();
        }
    }
}
