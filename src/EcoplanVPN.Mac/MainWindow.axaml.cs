using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

namespace EcoplanVPN.Mac;

public partial class MainWindow : Window
{
    private readonly Session _session = new();
    private CatalogSnapshot? _catalog;
    private string? _selectedId;
    private UpdateManifest? _update;
    private CancellationTokenSource? _connectCts;
    private bool _closing;

    public MainWindow()
    {
        InitializeComponent();
        VersionText.Text = "версия " + Product.Version + "  ·  xray " + Product.XrayVersion + "  ·  sing-box " + Product.SingBoxVersion;
        ModeHint.Text = "«Весь Mac» гонит трафик через tun (пароль администратора). «Только прокси» включает системный SOCKS — его понимают браузер и часть программ.";
        Log.Line += OnLog;
        ModeTun.IsCheckedChanged += (_, _) => UpdateModeHint();
        ModeProxy.IsCheckedChanged += (_, _) => UpdateModeHint();
        Closing += OnClosing;
        Opened += async (_, _) => await BootAsync();
    }

    private void UpdateModeHint()
    {
        ModeHint.Text = ModeTun.IsChecked == true
            ? "macOS спросит пароль. sing-box поднимет tun от администратора, без Network Extension."
            : "Системный SOCKS на 127.0.0.1. Браузеры пойдут в xray, он сам оставит .ru напрямую. Тоже нужен пароль.";
    }

    private void OnLog(string line) =>
        Dispatcher.UIThread.Post(() =>
        {
            LogBox.Text = Trim(LogBox.Text + line + "\n");
            LogBox.CaretIndex = LogBox.Text.Length;
        });

    private static string Trim(string text)
    {
        if (text.Length < 8000) return text;
        return text[^6000..];
    }

    private async Task BootAsync()
    {
        Log.Write("EcoplanVPN " + Product.Version + (Signing.IsConfigured() ? "" : " — сборка без OTA_PUB"));
        _selectedId = Settings.Get("Server");
        if (string.Equals(Settings.Get("Mode"), "proxy", StringComparison.OrdinalIgnoreCase))
            ModeProxy.IsChecked = true;
        try
        {
            _catalog = await CatalogClient.RefreshAsync(
                AppPaths.ServersCache, Settings.Serial(), Signing.PublicKeySpki, Log.Write, CancellationToken.None);
            if (_catalog != null)
                Settings.Set("ServersSerial", _catalog.Serial.ToString());
        }
        catch (Exception ex)
        {
            Log.Write("servers: " + ex.Message);
        }
        RenderCards();
        try
        {
            _update = await UpdaterClient.CheckAsync(CancellationToken.None);
            if (_update != null)
            {
                UpdateButton.IsVisible = true;
                UpdateButton.Content = "Обновить до " + _update.Version;
                Status.Text = "Доступна версия " + _update.Version + ". " + (_update.Notes ?? "");
            }
        }
        catch (Exception ex)
        {
            Log.Write("update: " + ex.Message);
        }
    }

    private void RenderCards()
    {
        Cards.Children.Clear();
        if (_catalog == null || _catalog.Servers.Count == 0)
        {
            CatalogHint.IsVisible = true;
            CatalogHint.Text = Signing.IsConfigured()
                ? "Список серверов не загрузился. Проверьте интернет и нажмите «Подключить» ещё раз — список обновится."
                : "В этой сборке нет публичного ключа подписи. Соберите scripts/build-mac.sh с client.env (тот же OTA_PUB, что у Windows). Без него servers.json не принимается.";
            return;
        }
        CatalogHint.IsVisible = false;
        if (_selectedId == null || _catalog.Servers.All(s => s.Id != _selectedId))
            _selectedId = _catalog.Servers.FirstOrDefault(s => s.OffNote == null)?.Id ?? _catalog.Servers[0].Id;
        foreach (var server in _catalog.Servers)
        {
            var selected = server.Id == _selectedId;
            var card = new Border
            {
                Background = new SolidColorBrush(Color.Parse(selected ? "#3A2030" : "#24141C")),
                BorderBrush = new SolidColorBrush(Color.Parse(selected ? "#FF4D88" : "#3A2430")),
                BorderThickness = new Avalonia.Thickness(1.5),
                CornerRadius = new Avalonia.CornerRadius(12),
                Padding = new Avalonia.Thickness(12, 10),
                Cursor = new Cursor(StandardCursorType.Hand),
                Tag = server.Id,
            };
            var title = server.Label;
            var sub = server.OffNote != null ? server.Subtitle + " · " + server.OffNote : server.Subtitle;
            card.Child = new StackPanel
            {
                Spacing = 2,
                Children =
                {
                    new TextBlock { Text = title, Foreground = Brushes.White, FontSize = 15, FontWeight = FontWeight.SemiBold },
                    new TextBlock { Text = sub, Foreground = new SolidColorBrush(Color.Parse("#D7A8B8")), FontSize = 12 },
                },
            };
            card.PointerPressed += (_, _) =>
            {
                if (_session.IsConnected || _session.IsBusy) return;
                _selectedId = server.Id;
                Settings.Set("Server", server.Id);
                RenderCards();
            };
            Cards.Children.Add(card);
        }
    }

    private ServerEntry? Selected() =>
        _catalog?.Servers.FirstOrDefault(s => s.Id == _selectedId);

    private ConnectMode CurrentMode() =>
        ModeProxy.IsChecked == true ? ConnectMode.Proxy : ConnectMode.Tun;

    private async void OnConnect(object? sender, RoutedEventArgs e)
    {
        if (_session.IsBusy) return;
        if (_session.IsConnected)
        {
            ConnectButton.IsEnabled = false;
            Status.Text = "Отключаю…";
            await _session.DisconnectAsync();
            ConnectButton.Content = "Подключить";
            ConnectButton.IsEnabled = true;
            Status.Text = "Всё выключено.";
            Subtitle.Text = "РФ напрямую, остальное через выбранный сервер";
            return;
        }

        _connectCts?.Cancel();
        _connectCts = new CancellationTokenSource();
        ConnectButton.IsEnabled = false;
        Status.Text = "Обновляю список серверов…";
        try
        {
            _catalog = await CatalogClient.RefreshAsync(
                AppPaths.ServersCache, Settings.Serial(), Signing.PublicKeySpki, Log.Write, _connectCts.Token)
                ?? _catalog;
            if (_catalog != null)
                Settings.Set("ServersSerial", _catalog.Serial.ToString());
            RenderCards();
            var server = Selected();
            if (server == null)
            {
                Status.Text = CatalogHint.Text;
                return;
            }
            if (server.OffNote != null)
            {
                Status.Text = server.Label + " сейчас выключен: " + server.OffNote;
                return;
            }
            Status.Text = "Проверяю ядро…";
            var mode = CurrentMode();
            Settings.Set("Mode", mode == ConnectMode.Proxy ? "proxy" : "tun");
            var result = await _session.ConnectAsync(_catalog!, server, mode, _connectCts.Token);
            Status.Text = result.Message;
            if (result.Ok)
            {
                ConnectButton.Content = "Отключить";
                Subtitle.Text = "Подключено · " + server.Label;
            }
        }
        catch (Exception ex)
        {
            Status.Text = ex.Message;
            Log.Write("ui: " + ex.Message);
        }
        finally
        {
            ConnectButton.IsEnabled = true;
        }
    }

    private async void OnPhone(object? sender, RoutedEventArgs e)
    {
        var server = Selected();
        if (_catalog == null || server == null)
        {
            Status.Text = "Сначала дождитесь списка серверов.";
            return;
        }
        var profile = EndpointProfile.Resolve(_catalog, server, "vision", out var error);
        if (profile == null)
        {
            Status.Text = error ?? "нет ссылки";
            return;
        }
        var link = profile.PhoneVisionLink();
        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard == null)
        {
            Status.Text = "Буфер недоступен.";
            return;
        }
        await clipboard.SetTextAsync(link);
        Status.Text = "Ссылка Vision скопирована.";
    }

    private async void OnUpdate(object? sender, RoutedEventArgs e)
    {
        if (_update == null || _session.IsConnected)
        {
            Status.Text = _session.IsConnected ? "Сначала отключитесь, затем обновитесь." : "Обновления нет.";
            return;
        }
        UpdateButton.IsEnabled = false;
        Status.Text = "Скачиваю " + _update.Version + "…";
        var result = await UpdaterClient.DownloadAsync(_update, CancellationToken.None);
        UpdateButton.IsEnabled = true;
        if (!result.Ok)
        {
            Status.Text = result.PathOrError;
            return;
        }
        Status.Text = "Образ проверен. Откройте его и замените программу.";
        if (OperatingSystem.IsMacOS())
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "/usr/bin/open",
                ArgumentList = { result.PathOrError },
                UseShellExecute = false,
            });
        }
    }

    private async void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        if (_closing) return;
        if (!_session.IsConnected && !_session.IsBusy) return;
        e.Cancel = true;
        _closing = true;
        _connectCts?.Cancel();
        try { await _session.DisconnectAsync(); }
        catch { }
        Close();
    }
}
