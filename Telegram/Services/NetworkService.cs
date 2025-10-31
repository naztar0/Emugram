//
// Copyright Fela Ameghino 2015-2025
//
// Distributed under the GNU General Public License v3.0. (See accompanying
// file LICENSE or copy at https://www.gnu.org/licenses/gpl-3.0.txt)
//
using System;
using System.Threading.Tasks;
using Telegram.Common;
using Telegram.Native;
using Telegram.Td.Api;
using Windows.Foundation;
using Windows.Networking.Connectivity;

namespace Telegram.Services
{
    public interface INetworkService
    {
        void Reconnect();

        Task<int> GetSystemProxyId();
        Task UpdateProxyAsync(int proxyId);

        NetworkType Type { get; }
        bool IsMetered { get; }

        bool UseSystemProxy { get; set; }
        event EventHandler<Proxy> ProxyChanged;
    }

    public partial class NetworkService : INetworkService
    {
        private readonly IClientService _clientService;
        private readonly ISettingsService _settingsService;
        private readonly IEventAggregator _aggregator;

        private readonly HttpProxyWatcher _watcher;
        private readonly EventDebouncer<bool> _debouncer;

        private TaskCompletionSource<int> _initialize = new();

        public NetworkService(IClientService clientService, ISettingsService settingsService, IEventAggregator aggregator)
        {
            _clientService = clientService;
            _settingsService = settingsService;
            _aggregator = aggregator;

            _watcher = HttpProxyWatcher.Current;
            _debouncer = new EventDebouncer<bool>(Constants.HoldingThrottle,
                handler => _watcher.Changed += new TypedEventHandler<HttpProxyWatcher, bool>(handler),
                handler => _watcher.Changed -= new TypedEventHandler<HttpProxyWatcher, bool>(handler), true);

            NetworkInformation.NetworkStatusChanged += OnNetworkStatusChanged;

            try
            {
                Update(NetworkInformation.GetInternetConnectionProfile());
            }
            catch { }

            if (UseSystemProxy)
            {
                ProxyChanged += OnProxyChanged;
            }

            aggregator.Subscribe<UpdateAuthorizationState>(this, Handle);

            Handle(new UpdateAuthorizationState(_clientService.AuthorizationState));
        }

        private void Handle(UpdateAuthorizationState update)
        {
            if (update.AuthorizationState is AuthorizationStateWaitPhoneNumber or AuthorizationStateReady)
            {
                _ = UpdateSystemProxy();
            }
            else if (update.AuthorizationState is AuthorizationStateClosed)
            {
                _initialize = new TaskCompletionSource<int>();
            }
        }

        public bool UseSystemProxy
        {
            get => _settingsService.UseSystemProxy;
            set
            {
                if (_settingsService.UseSystemProxy != value)
                {
                    _settingsService.UseSystemProxy = value;

                    if (value)
                    {
                        ProxyChanged += OnProxyChanged;
                    }
                    else
                    {
                        ProxyChanged -= OnProxyChanged;
                    }
                }
            }
        }

        private void OnProxyChanged(object sender, Proxy e)
        {
            // This is used only to keep the event subscribed.
            // No action should be performed here.
        }

        private event EventHandler<Proxy> _proxyChanged;
        public event EventHandler<Proxy> ProxyChanged
        {
            add
            {
                if (_proxyChanged == null)
                {
                    _debouncer.Invoked += OnProxyChanged;
                }

                _proxyChanged += value;
            }
            remove
            {
                _proxyChanged -= value;

                if (_proxyChanged == null)
                {
                    _debouncer.Invoked -= OnProxyChanged;
                }
            }
        }

        private async void OnProxyChanged(object sender, bool args)
        {
            _proxyChanged?.Invoke(this, await UpdateSystemProxy());
        }

        public Task<int> GetSystemProxyId()
        {
            return _initialize.Task;
        }

        public async Task UpdateProxyAsync(int proxyId)
        {
            var systemProxyId = await _initialize.Task;

            if (proxyId == 0)
            {
                UseSystemProxy = false;
                await _clientService.SendAsync(new DisableProxy());
            }
            else if (proxyId == systemProxyId)
            {
                UseSystemProxy = true;
                await UpdateSystemProxy();
            }

            UseSystemProxy = false;
            await _clientService.SendAsync(new EnableProxy(proxyId));
        }

        private async Task<Proxy> UpdateSystemProxy()
        {
            if (_settingsService.UseSystemProxy && !_watcher.IsEnabled)
            {
                _clientService.Send(new DisableProxy());
            }

            string host;
            int port;
            if (TryCreateUri(_watcher.Server, out Uri result))
            {
                host = result.Host;
                port = result.Port;
            }
            else
            {
                host = "localhost";
                port = 80;
            }

            var enabled = _settingsService.UseSystemProxy && _watcher.IsEnabled;

            var proxyId = await _clientService.SendAsync(new GetOption(OptionsService.R.SystemProxy)) as OptionValueInteger;
            if (proxyId != null)
            {
                _initialize.TrySetResult((int)proxyId.Value);
                return await _clientService.SendAsync(new EditProxy((int)proxyId.Value, host, port, enabled, new ProxyTypeHttp())) as Proxy;
            }

            var proxy = await _clientService.SendAsync(new AddProxy(host, port, enabled, new ProxyTypeHttp())) as Proxy;
            if (proxy != null)
            {
                _clientService.Options.SystemProxy = proxy.Id;
                _initialize.TrySetResult(proxy.Id);
                return proxy;
            }

            _initialize.TrySetResult(0);
            return null;
        }

        private bool TryCreateUri(string server, out Uri result)
        {
            var query = server.Split(';');

            foreach (var part in query)
            {
                var split = part.Split('=');
                if (split.Length == 2 && string.Equals(split[0], "http"))
                {
                    return MessageHelper.TryCreateUri(split[1], out result);
                }
                else if (split.Length == 1)
                {
                    return MessageHelper.TryCreateUri(split[0], out result);
                }
            }

            result = null;
            return false;
        }

        public void Reconnect()
        {
            _clientService.Send(new SetNetworkType(_type));
        }

        private void OnNetworkStatusChanged(object sender)
        {
            try
            {
                Update(NetworkInformation.GetInternetConnectionProfile());
            }
            catch { }
        }

        private void Update(ConnectionProfile profile)
        {
            _clientService.Send(new SetNetworkType(_type = GetNetworkType(profile)));
        }

        private NetworkType GetNetworkType(ConnectionProfile profile)
        {
            if (profile == null)
            {
                //return new NetworkTypeNone();
                return new NetworkTypeWiFi();
            }

            var cost = profile.GetConnectionCost();
            if (cost != null)
            {
                IsMetered = cost.NetworkCostType is not NetworkCostType.Unrestricted and not NetworkCostType.Unknown;
            }
            else
            {
                IsMetered = false;
            }

            var level = profile.GetNetworkConnectivityLevel();
            if (level is NetworkConnectivityLevel.LocalAccess or NetworkConnectivityLevel.None)
            {
                //return new NetworkTypeNone();
                return new NetworkTypeWiFi();
            }

            if (cost != null && cost.Roaming)
            {
                return new NetworkTypeMobileRoaming();
            }
            else if (profile.IsWlanConnectionProfile)
            {
                return new NetworkTypeWiFi();
            }
            else if (profile.IsWwanConnectionProfile)
            {
                return new NetworkTypeMobile();
            }

            // This is most likely cable connection.
            //return new NetworkTypeOther();
            return new NetworkTypeWiFi();
        }

        private NetworkType _type = new NetworkTypeOther();
        public NetworkType Type
        {
            get => _type;
            private set => _type = value;
        }

        private bool _isMetered;
        public bool IsMetered
        {
            get => _isMetered;
            set => _isMetered = value;
        }
    }
}
