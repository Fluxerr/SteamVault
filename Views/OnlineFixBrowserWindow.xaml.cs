using Microsoft.Web.WebView2.Core;
using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media.Animation;

namespace SteamVault.Views;

public partial class OnlineFixBrowserWindow : Window
{
    private readonly string _gameName;
    private readonly string _targetDownloadDir;
    private bool _isInitialized;
    private IntPtr _windowHwnd = IntPtr.Zero;

    private const int WM_MOUSEACTIVATE = 0x0021;
    private const int MA_ACTIVATEANDEAT = 3;

    // Ad/redirect domains that should be blocked entirely
    private static readonly string[] AdParts =
    {
        "popunder","popads","propeller","clickadu","adtelligent","adsterra",
        "galaksion","cpmstar","adcash","popmyads","trafficstars","popmonetizer",
        "ad-maven","mgid","outbrain","taboola","exoclick","popcash",
        "juicyads","adnow","revcontent","contentad","bidvertiser","infolinks",
        "media.net","adblade","adversal","hilltopads","popads.net","ad-center",
        "trafficjunky","plugrush","adspyglass","popad","monetag","admaven",
        "adinplay","adservice","doubleclick","googlesyndication","pagead2",
        "amazon-adsystem","criteo","openx","pubmatic","rubiconproject","casalemedia",
        "adsrvr","adnxs","moatads","exdynsrv","xmlclick","onclasrv","s-go",
        "adexchange","adform","adroll","smaato","turn.com","adsafeprotected",
        "zedo","advertising","adzerk","adtech","pushprofit","push-ad",
        "pushwelcome","notifpush"
    };

    // Link shorteners/redirects used by online-fix.me for download buttons.
    // Blocking these causes the page to fall back to the direct download URL,
    // matching the behavior of Chrome with uBlock Origin.
    private static readonly string[] LinkShorteners =
    {
        "lootlabs", "loot-reward", "loot-reward.com", "loot-link",
        "lootlabs.gg", "loot-link.com", "loot-links.com", "lootlab",
        "linkvertise", "link-to", "ouo.io", "ouo.press",
        "shorte.st", "shortconnect", "bc.vc", "adf.ly", "adfoc.us", "linktiny",
        "shrinkme", "za.gl", "aylink", "clicksfly", "techymozo", "mboost",
        "linkshrink", "lnk2", "urlgo", "tinyurl.one",
        "sub2unlock", "sub2get", "social-unlock", "sociallocker",
        "direct-link.net", "link-center.net",
        "exe.io", "fc.lc", "gplinks.co",
        "earnl.xyz", "link1s.com",
        "shrinke.me", "shrinkearn.com",
        "oke.io", "droplink.co", "link-hub.net"
    };

    // Lightweight JS — only removes ad iframes/overlays and neutralises window.open.
    // Does NOT override fetch/XHR/Object.defineProperty which breaks site scripts.
    private const string AdBlockJS = @"
(function(){
    var B=[""popunder"",""popads"",""propeller"",""clickadu"",""adtelligent"",
        ""adsterra"",""galaksion"",""cpmstar"",""adcash"",""popmyads"",""trafficstars"",
        ""popmonetizer"",""ad-maven"",""mgid"",""outbrain"",""taboola"",""lootlab"",
        ""loot-reward"",""loot-link"",""lootlabs"",""linkvertise"",
        ""exoclick"",""popcash"",""juicyads"",""adnow"",""revcontent"",""contentad"",
        ""bidvertiser"",""infolinks"",""media.net"",""adblade"",""adversal"",
        ""hilltopads"",""popads.net"",""ad-center"",""trafficjunky"",""plugrush"",
        ""adspyglass"",""popad"",""monetag"",""admaven"",""adinplay"",""adservice"",
        ""doubleclick"",""googlesyndication"",""pagead2"",""amazon-adsystem"",
        ""criteo"",""openx"",""pubmatic"",""rubiconproject"",""casalemedia"",""adsrvr"",
        ""adnxs"",""moatads"",""exdynsrv"",""xmlclick"",""onclasrv"",""s-go""];
    function bad(u){var l=(u||'').toLowerCase();for(var i=0;i<B.length;i++)if(l.indexOf(B[i])>=0)return true;return false;}
    
    function clean(){
        // 1. Remove ad overlays/iframes
        var fs=document.querySelectorAll('iframe');
        for(var i=0;i<fs.length;i++){
            var f=fs[i];if(!f.parentNode)continue;
            if(f.offsetHeight>=window.innerHeight*0.6){f.remove();continue;}
            if(bad(f.src||''))f.remove();
        }
        var ds=document.querySelectorAll('div');
        for(var j=0;j<ds.length;j++){
            var d=ds[j],z=parseInt(d.style.zIndex||0);
            if(z>9998&&d.offsetHeight>window.innerHeight*0.4)d.remove();
        }

        // 2. Prevent JS from hijacking download links by replacing them with clean clones
        var links=document.querySelectorAll('a');
        for(var k=0;k<links.length;k++){
            var a=links[k];
            var href=(a.href||'').toLowerCase();
            // If it's a direct download link or torrent, clean it
            if(!a.dataset.cleaned && (href.indexOf('uploads.online-fix')>=0 || href.indexOf('.torrent')>=0)){
                var clone=a.cloneNode(true);
                clone.dataset.cleaned='true';
                if(a.parentNode) a.parentNode.replaceChild(clone,a);
            }
        }
    }
    
    clean();setInterval(clean,1000);
    try{new MutationObserver(clean).observe(document.documentElement,{childList:true,subtree:true});}catch(e){}
    
    var _open=window.open;
    window.open=function(u,n,f){
        if(!u||typeof u!=='string')return null;
        var l=u.toLowerCase();
        if(l.indexOf('online-fix')>=0){try{window.location.href=u;}catch(e){} return null;}
        return null;
    };
    
    // Auto-Translate Russian to English
    if(window.location.hostname.indexOf('online-fix.me') >= 0) {
        var style = document.createElement('style');
        style.innerHTML = 'body { top: 0px !important; } .skiptranslate, .goog-te-banner-frame { display: none !important; }';
        document.head.appendChild(style);
        
        var d = document.createElement('div');
        d.id = 'google_translate_element';
        d.style.display = 'none';
        document.body.appendChild(d);
        
        window.googleTranslateElementInit = function() {
            new google.translate.TranslateElement({pageLanguage: 'ru', includedLanguages: 'en', autoDisplay: false}, 'google_translate_element');
            setTimeout(function() {
                var select = document.querySelector('select.goog-te-combo');
                if(select) {
                    select.value = 'en';
                    select.dispatchEvent(new Event('change'));
                }
            }, 1000);
        };
        
        var s = document.createElement('script');
        s.src = 'https://translate.google.com/translate_a/element.js?cb=googleTranslateElementInit';
        document.head.appendChild(s);
    }
})();";

    public event Action<string>? DownloadCompleted;

    public OnlineFixBrowserWindow(string gameName)
    {
        InitializeComponent();
        _gameName = gameName;
        _targetDownloadDir = AppDomain.CurrentDomain.BaseDirectory;
        Loaded += OnLoaded;
        SourceInitialized += OnSourceInitialized;
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        _windowHwnd = new WindowInteropHelper(this).Handle;
        var src = HwndSource.FromHwnd(_windowHwnd);
        if (src != null) src.AddHook(WndProcHook);
    }

    private IntPtr WndProcHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_MOUSEACTIVATE && Browser.Visibility == Visibility.Visible)
        {
            Dispatcher.InvokeAsync(() => Browser.Focus(), System.Windows.Threading.DispatcherPriority.Input);
            handled = true;
            return (IntPtr)MA_ACTIVATEANDEAT;
        }
        return IntPtr.Zero;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e) => await InitializeBrowserAsync();

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
            WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
        else DragMove();
    }

    private void BackButton_Click(object sender, RoutedEventArgs e)
    { if (_isInitialized && Browser.CoreWebView2.CanGoBack) Browser.CoreWebView2.GoBack(); }
    private void ForwardButton_Click(object sender, RoutedEventArgs e)
    { if (_isInitialized && Browser.CoreWebView2.CanGoForward) Browser.CoreWebView2.GoForward(); }
    private void RefreshButton_Click(object sender, RoutedEventArgs e)
    { if (_isInitialized) Browser.CoreWebView2.Reload(); }

    private async Task InitializeBrowserAsync()
    {
        try
        {
            var userDataFolder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "SteamVault", "WebView2");
            Directory.CreateDirectory(userDataFolder);
            var env = await CoreWebView2Environment.CreateAsync(null, userDataFolder);
            await Browser.EnsureCoreWebView2Async(env);
            _isInitialized = true;

            Browser.CoreWebView2.Settings.UserAgent =
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/137.0.0.0 Safari/537.36";

            // Inject the lightweight JS adblock (DOM cleanup + window.open block)
            await Browser.CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync(AdBlockJS);

            // Targeted filter: inject correct Referer for download requests.
            // Only fires for uploads.online-fix* — no perf impact unlike wildcard.
            Browser.CoreWebView2.AddWebResourceRequestedFilter(
                "*://*.online-fix.me/*", CoreWebView2WebResourceContext.All);
            Browser.CoreWebView2.AddWebResourceRequestedFilter(
                "*://online-fix.me/*", CoreWebView2WebResourceContext.All);
            Browser.CoreWebView2.WebResourceRequested += OnWebResourceRequested;

            // Wire up events
            Browser.CoreWebView2.NavigationStarting += OnNavigationStarting;
            Browser.CoreWebView2.NavigationCompleted += OnNavigationCompleted;
            Browser.CoreWebView2.DownloadStarting += OnDownloadStarting;
            Browser.CoreWebView2.NewWindowRequested += OnNewWindowRequested;
            Browser.CoreWebView2.PermissionRequested += OnPermissionRequested;

            var url = $"https://online-fix.me/index.php?do=search&subaction=search&story={Uri.EscapeDataString(_gameName)}";
            Browser.CoreWebView2.Navigate(url);
        }
        catch (Exception ex)
        {
            LoadingOverlay.Visibility = Visibility.Collapsed;
            System.Windows.MessageBox.Show($"Browser error: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Warning);
            Close();
        }
    }

    // ──────────────────────────────────────────────────────────
    //  Referer fix: uploads.online-fix.me returns 401 without a
    //  valid Referer from online-fix.me.  After we cancel the
    //  loot-reward redirect, the fallback navigation can lose the
    //  Referer, so we force it here.
    // ──────────────────────────────────────────────────────────

    private void OnWebResourceRequested(object? sender, CoreWebView2WebResourceRequestedEventArgs e)
    {
        e.Request.Headers.SetHeader("Referer", "https://online-fix.me/");
    }

    // ──────────────────────────────────────────────────────────
    //  Navigation-level: cancel redirects to loot-reward / ad sites
    //  This is the key layer — when online-fix.me tries to redirect
    //  you to loot-reward.com, this cancels it so the page falls
    //  back to the direct download URL (same as Chrome + uBlock).
    // ──────────────────────────────────────────────────────────

    private void OnNavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs e)
    {
        var uri = (e.Uri ?? "").ToLowerInvariant();

        // Only block if the URL is NOT on online-fix.me itself
        if (uri.Contains("online-fix.me") || uri.Contains("uploads.online-fix"))
            return;

        foreach (var s in LinkShorteners)
        {
            if (uri.Contains(s))
            {
                e.Cancel = true;
                // Bypass loot-reward directly to the game's upload directory
                var bypassUrl = $"https://uploads.online-fix.me:2053/uploads/{Uri.EscapeDataString(_gameName)}/";
                Browser.CoreWebView2.Navigate(bypassUrl);
                return;
            }
        }

        foreach (var d in AdParts)
        {
            if (uri.Contains(d))
            {
                e.Cancel = true;
                return;
            }
        }
    }

    // ──────────────────────────────────────────────────────────
    //  Block permission requests (push notifications, etc.)
    // ──────────────────────────────────────────────────────────

    private void OnPermissionRequested(object? sender, CoreWebView2PermissionRequestedEventArgs e)
    {
        e.State = CoreWebView2PermissionState.Deny;
    }

    // ──────────────────────────────────────────────────────────
    //  Popup handling — block ad popups, allow legit navigations
    // ──────────────────────────────────────────────────────────

    private void OnNewWindowRequested(object? sender, CoreWebView2NewWindowRequestedEventArgs e)
    {
        e.Handled = true;
        var uri = (e.Uri ?? "").ToLowerInvariant();

        // WHITELIST: only allow online-fix.me URLs through.
        // Everything else (ads, trackers, loot-reward, unknown domains)
        // is silently blocked. This is far more reliable than trying to
        // maintain a blacklist of every ad domain.
        if (!uri.Contains("online-fix"))
            return;

        Browser.CoreWebView2.Navigate(e.Uri);
    }

    private void OnNavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        if (LoadingOverlay.Visibility == Visibility.Visible)
        {
            Browser.Visibility = Visibility.Visible;
            Browser.Focus();
            var fadeOut = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(350))
            { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
            fadeOut.Completed += (s, ev) => LoadingOverlay.Visibility = Visibility.Collapsed;
            LoadingOverlay.BeginAnimation(OpacityProperty, fadeOut);
        }
        Dispatcher.InvokeAsync(() => UrlBar.Text = Browser.CoreWebView2.Source);
    }

    private void OnDownloadStarting(object? sender, CoreWebView2DownloadStartingEventArgs e)
    {
        var ext = Path.GetExtension(e.ResultFilePath);
        if (string.Equals(ext, ".rar", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(ext, ".zip", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(ext, ".7z", StringComparison.OrdinalIgnoreCase))
        {
            var filename = Path.GetFileName(e.ResultFilePath);
            e.ResultFilePath = Path.Combine(_targetDownloadDir, filename);
            e.Handled = true;
            Dispatcher.Invoke(() => { NotificationText.Text = $"Downloading {filename}..."; ShowToast(); });
            e.DownloadOperation.StateChanged += (s, ev) => Dispatcher.Invoke(() =>
            {
                switch (e.DownloadOperation.State)
                {
                    case CoreWebView2DownloadState.Completed:
                        NotificationText.Text = $"Done: {filename}"; ShowToast();
                        DownloadCompleted?.Invoke(e.ResultFilePath);
                        Task.Delay(1500).ContinueWith(_ => Dispatcher.Invoke(() => { try { Close(); } catch { } }));
                        break;
                    case CoreWebView2DownloadState.Interrupted:
                        NotificationText.Text = "Download failed"; ShowToast();
                        break;
                }
            });
        }
    }

    private void ShowToast()
    {
        NotificationToast.Opacity = 0; ToastTranslate.Y = 20;
        var sb = new Storyboard();
        var fi = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(300));
        Storyboard.SetTarget(fi, NotificationToast);
        Storyboard.SetTargetProperty(fi, new PropertyPath("Opacity"));
        var su = new DoubleAnimation(20, 0, TimeSpan.FromMilliseconds(300))
        { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
        Storyboard.SetTarget(su, NotificationToast);
        Storyboard.SetTargetProperty(su, new PropertyPath("(UIElement.RenderTransform).(TranslateTransform.Y)"));
        sb.Children.Add(fi); sb.Children.Add(su); sb.Begin();
        Task.Delay(4000).ContinueWith(_ => Dispatcher.Invoke(() =>
        {
            try { NotificationToast.BeginAnimation(OpacityProperty, new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(300))); }
            catch { }
        }));
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        if (_isInitialized)
        {
            try
            {
                Browser.CoreWebView2.WebResourceRequested -= OnWebResourceRequested;
                Browser.CoreWebView2.NavigationStarting -= OnNavigationStarting;
                Browser.CoreWebView2.DownloadStarting -= OnDownloadStarting;
                Browser.CoreWebView2.NewWindowRequested -= OnNewWindowRequested;
                Browser.CoreWebView2.NavigationCompleted -= OnNavigationCompleted;
                Browser.CoreWebView2.PermissionRequested -= OnPermissionRequested;
            }
            catch { }
        }
        try { Browser.Dispose(); } catch { }
    }
}