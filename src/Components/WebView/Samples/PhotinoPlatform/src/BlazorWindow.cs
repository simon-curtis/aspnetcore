// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.FileProviders;
using PhotinoNET;

namespace Microsoft.AspNetCore.Components.WebView.Photino;

/// <summary>
/// A window containing a Blazor web view.
/// </summary>
public class BlazorWindow
{
    private readonly PhotinoWindow _window;
    private readonly PhotinoWebViewManager _manager;
    private readonly string _pathBase;

    /// <summary>
    /// Constructs an instance of <see cref="BlazorWindow"/>.
    /// </summary>
    /// <param name="title">The window title.</param>
    /// <param name="hostPage">The path to the host page.</param>
    /// <param name="services">The service provider.</param>
    /// <param name="configureWindow">A callback that configures the window.</param>
    /// <param name="pathBase">The pathbase for the application. URLs will be resolved relative to this.</param>
    public BlazorWindow(
        string title,
        string hostPage,
        IServiceProvider services,
        Action<PhotinoWindowOptions>? configureWindow = null,
        string? pathBase = null)
    {
        _window = new PhotinoWindow(title, options =>
        {
            options.CustomSchemeHandlers.Add(PhotinoWebViewManager.BlazorAppScheme, HandleWebRequest);
            configureWindow?.Invoke(options);
        }, width: 1600, height: 1200, left: 300, top: 300);

        // We assume the host page is always in the root of the content directory, because it's
        // unclear there's any other use case. We can add more options later if so.
        var contentRootDir = Path.GetDirectoryName(Path.GetFullPath(hostPage))!;
        var hostPageRelativePath = Path.GetRelativePath(contentRootDir, hostPage);
        var fileProvider = new PhysicalFileProvider(contentRootDir);

        var dispatcher = new PhotinoDispatcher(_window);
        var jsComponents = new JSComponentConfigurationStore();

        _pathBase = (pathBase ?? string.Empty);
        if (!_pathBase.EndsWith('/'))
        {
            _pathBase += "/";
        }
        var appBaseUri = new Uri(new Uri(PhotinoWebViewManager.AppBaseOrigin), _pathBase);

        _manager = new PhotinoWebViewManager(_window, services, dispatcher, appBaseUri, fileProvider, jsComponents, hostPageRelativePath);
        RootComponents = new BlazorWindowRootComponents(_manager, jsComponents);
    }

    /// <summary>
    /// Gets the underlying <see cref="PhotinoWindow"/>.
    /// </summary>
    public PhotinoWindow Photino => _window;

    /// <summary>
    /// Gets configuration for the root components in the window.
    /// </summary>
    public BlazorWindowRootComponents RootComponents { get; }

    private string _latestControlDivValue;

    /// <summary>
    /// Shows the window and waits for it to be closed.
    /// </summary>
    public void Run(bool isTestMode)
    {
        const string NewControlDivValueMessage = "wvt:NewControlDivValue";
        var isWebViewReady = false;

        if (isTestMode)
        {
            _window.RegisterWebMessageReceivedHandler((s, msg) =>
            {
                if (!msg.StartsWith("__bwv:", StringComparison.Ordinal))
                {
                    if (msg == "wvt:Started")
                    {
                        isWebViewReady = true;
                    }
                    else if (msg.StartsWith(NewControlDivValueMessage, StringComparison.Ordinal))
                    {
                        _latestControlDivValue = msg.Substring(NewControlDivValueMessage.Length + 1);
                    }
                    Debug.WriteLine(msg);
                }
            });
        }

        _manager.Navigate(_pathBase);

        if (isTestMode)
        {
            Task.Run(async () =>
            {
                // 1. Wait for WebView ready
                Debug.WriteLine($"Waiting for WebView ready...");
                var isWebViewReadyRetriesLeft = 5;
                while (!isWebViewReady)
                {
                    Debug.WriteLine($"WebView not ready yet, waiting 1sec...");
                    await Task.Delay(1000);
                    isWebViewReadyRetriesLeft--;
                    if (isWebViewReadyRetriesLeft == 0)
                    {
                        Debug.WriteLine($"WebView never became ready, failing the test...");
                        // TODO: Fail the test
                        return;
                    }
                }
                Debug.WriteLine($"WebView is ready!");

                // 2. Check TestPage starting state
                if (!await WaitForControlDiv(controlValueToWaitFor: "0"))
                {
                    // TODO: Fail the test
                    return;
                }

                // 3. Click a button
                _window.SendWebMessage($"wvt:ClickButton:incrementButton");

                // 4. Check TestPage is updated after button click
                if (!await WaitForControlDiv(controlValueToWaitFor: "1"))
                {
                    // TODO: Fail the test
                    return;
                }

                // 5. If we get here, it all worked!
                Debug.WriteLine($"All tests passed!");

                _window.Close();
            });
        }

        _window.WaitForClose();
    }

    const int MaxWaitTimes = 30;
    const int WaitTimeInMS = 250;

    public async Task<bool> WaitForControlDiv(string controlValueToWaitFor)
    {

        for (var i = 0; i < MaxWaitTimes; i++)
        {
            // Tell WebView to report the current controlDiv value (this is inside the loop because
            // it's possible for this to execute before the WebView has finished processing previous
            // C#-generated events, such as WebView button clicks).
            Debug.WriteLine($"Asking WebView for current controlDiv value...");
            _window.SendWebMessage($"wvt:GetControlDivValue");

            // And wait for the value to appear
            if (_latestControlDivValue == controlValueToWaitFor)
            {
                Debug.WriteLine($"WebView reported the expected controlDiv value of {controlValueToWaitFor}!");
                return true;
            }
            Debug.WriteLine($"Waiting for controlDiv to have value '{controlValueToWaitFor}', but it's still '{_latestControlDivValue}', so waiting {WaitTimeInMS}ms.");
            await Task.Delay(WaitTimeInMS);
        }

        Debug.WriteLine($"Waited {MaxWaitTimes * WaitTimeInMS}ms but couldn't get controlDiv to have value '{controlValueToWaitFor}' (last value is '{_latestControlDivValue}').");
        return false;
    }

    private Stream HandleWebRequest(string url, out string contentType)
        => _manager.HandleWebRequest(url, out contentType!)!;
}
