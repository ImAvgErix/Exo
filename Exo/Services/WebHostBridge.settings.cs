using System.Text.Json;

using System.Text.Json.Serialization;

using Exo.Helpers;

using Exo.Models;

using Exo.ViewModels;

using Microsoft.UI.Dispatching;

using Microsoft.Web.WebView2.Core;



namespace Exo.Services;



/// <summary>

/// JSON-RPC bridge between the React UI (WebView2) and native optimizer services.

/// UI owns pixels; this host owns elevation, scripts, and live machine reads.

/// </summary>

public sealed partial class WebHostBridge

{

    private object BuildSettings()

    {

        var s = _services.Settings.Current;

        return new

        {

            appVersion = typeof(App).Assembly.GetName().Version?.ToString(3) ?? "4.5.5",

            checkForUpdatesOnLaunch = s.CheckForUpdatesOnLaunch,

            welcomePromptSeen = s.WelcomePromptSeen,

            buyMeACoffeeUrl = BuyMeACoffeeUrl,

            issuesUrl = IssuesUrl,

            textColour = s.TextColour,

            textSize = s.TextSize,

            experimentalDefaults = new

            {

                discord = s.ExperimentalDiscord,

                steam = s.ExperimentalSteam,

                internet = s.ExperimentalInternet,

                nvidia = s.ExperimentalNvidia

            }

        };

    }



    /// <summary>

    /// In-app changelog from bundled CHANGELOG.md (repo root next to app).

    /// Parsed into version sections for the glass settings sheet.

    /// </summary>

    private object BuildChangelog()

    {

        try

        {

            var path = ResolveChangelogPath();

            if (path is null || !File.Exists(path))

            {

                return new

                {

                    ok = false,

                    message = "Changelog file not found.",

                    sections = Array.Empty<object>()

                };

            }



            var text = File.ReadAllText(path);

            var sections = ParseChangelogMarkdown(text);

            return new

            {

                ok = true,

                path,

                sections

            };

        }

        catch (Exception ex)

        {

            return new

            {

                ok = false,

                message = ex.Message,

                sections = Array.Empty<object>()

            };

        }

    }



    private static string? ResolveChangelogPath()

    {

        // Published: next to Exo.exe. Dev: repo root / AppDirectory parents.

        var candidates = new[]

        {

            Path.Combine(AppContext.BaseDirectory, "CHANGELOG.md"),

            Path.Combine(PathHelper.AppDirectory, "CHANGELOG.md"),

            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "CHANGELOG.md")),

            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "CHANGELOG.md")),

        };

        foreach (var c in candidates)

        {

            try

            {

                if (File.Exists(c)) return c;

            }

            catch { /* skip */ }

        }

        return null;

    }



    /// <summary>Parse ## version headers + - bullets into UI sections (newest first, cap 40).</summary>

    internal static List<object> ParseChangelogMarkdown(string text)

    {

        var sections = new List<object>();

        string? version = null;

        var bullets = new List<string>();



        void Flush()

        {

            if (version is null) return;

            sections.Add(new

            {

                version,

                bullets = bullets.ToArray()

            });

            bullets.Clear();

            version = null;

        }



        foreach (var raw in text.Replace("\r\n", "\n").Split('\n'))

        {

            var line = raw.TrimEnd();

            if (line.StartsWith("## ", StringComparison.Ordinal))

            {

                Flush();

                version = line[3..].Trim().TrimStart('v', 'V');

                continue;

            }

            if (version is null) continue;

            var t = line.Trim();

            if (t.StartsWith("- ", StringComparison.Ordinal) || t.StartsWith("* ", StringComparison.Ordinal))

            {

                var b = t[2..].Trim();

                // Drop markdown bold markers for cleaner in-app text

                b = b.Replace("**", "", StringComparison.Ordinal);

                if (b.Length > 0) bullets.Add(b);

            }

        }

        Flush();



        // Newest first already if file is newest-first; cap for UI

        if (sections.Count > 40)

            sections = sections.Take(40).ToList();

        return sections;

    }



    private object SetSettings(JsonElement p, bool hasParams)

    {

        if (!hasParams || p.ValueKind != JsonValueKind.Object)

            return BuildSettings();



        _services.Settings.Update(s =>

        {

            if (p.TryGetProperty("checkForUpdatesOnLaunch", out var u) &&

                (u.ValueKind is JsonValueKind.True or JsonValueKind.False))

                s.CheckForUpdatesOnLaunch = u.ValueKind == JsonValueKind.True;

            if (p.TryGetProperty("welcomePromptSeen", out var w) &&

                (w.ValueKind is JsonValueKind.True or JsonValueKind.False))

                s.WelcomePromptSeen = w.ValueKind == JsonValueKind.True;

            // Only the values the shell can actually render. An unknown string here would

            // persist and then paint nothing, leaving the window with no text colour at all.

            if (p.TryGetProperty("textColour", out var tc) && tc.ValueKind == JsonValueKind.String &&

                tc.GetString() is "white" or "grey")

                s.TextColour = tc.GetString()!;

            if (p.TryGetProperty("textSize", out var ts) && ts.ValueKind == JsonValueKind.String &&

                ts.GetString() is "small" or "normal" or "large")

                s.TextSize = ts.GetString()!;

        });

        return BuildSettings();

    }



    /// <summary>Last URL + tick so a double-attached bridge or double-click cannot open two tabs.</summary>

    private string? _lastOpenUrl;

    private long _lastOpenUrlTick;



    private object OpenExternalUrl(JsonElement p, bool hasParams)

    {

        try

        {

            var url = ReadString(p, hasParams, "url")?.Trim();

            if (string.IsNullOrWhiteSpace(url))

                url = BuyMeACoffeeUrl;

            // Only allow http(s) so the bridge cannot launch local files/shells.

            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||

                (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))

                return new { ok = false, message = "Only http(s) links are allowed." };



            if (!TryOpenUrlOnce(uri.AbsoluteUri))

                return new { ok = true, url = uri.AbsoluteUri, deduped = true };



            return new { ok = true, url = uri.AbsoluteUri };

        }

        catch (Exception ex)

        {

            return new { ok = false, message = ex.Message };

        }

    }



    private object OpenLogsFolder()

    {

        try

        {

            var logs = Path.Combine(

                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),

                "Exo", "logs");

            Directory.CreateDirectory(logs);

            // Always open the folder (user asked for logs directory, not newest file).

            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo

            {

                FileName = "explorer.exe",

                Arguments = "\"" + logs + "\"",

                UseShellExecute = true

            });

            return new { ok = true, path = logs, folder = logs };

        }

        catch (Exception ex)

        {

            return new { ok = false, message = ex.Message };

        }

    }



    private object OpenIssues()

    {

        try

        {

            if (!TryOpenUrlOnce(IssuesUrl))

                return new { ok = true, deduped = true };

            return new { ok = true };

        }

        catch (Exception ex)

        {

            return new { ok = false, message = ex.Message };

        }

    }



    /// <returns>false if the same URL was opened within the last 800ms (skip second tab).</returns>

    private bool TryOpenUrlOnce(string absoluteUrl)

    {

        var now = Environment.TickCount64;

        if (_lastOpenUrl is not null &&

            string.Equals(_lastOpenUrl, absoluteUrl, StringComparison.OrdinalIgnoreCase) &&

            now - _lastOpenUrlTick < 800)

            return false;



        _lastOpenUrl = absoluteUrl;

        _lastOpenUrlTick = now;

        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo

        {

            FileName = absoluteUrl,

            UseShellExecute = true

        });

        return true;

    }



    private object OpenNvidiaControlPanel()

    {

        try

        {

            if (_services.NvidiaPanel.TryLaunchControlPanel(out var error))

                return new { ok = true };

            return new { ok = false, message = error ?? "NVIDIA Control Panel is not installed." };

        }

        catch (Exception ex)

        {

            return new { ok = false, message = ex.Message };

        }

    }



    /// <summary>

    /// Check GitHub latest; when an update is available, download + quiet-install

    /// without a native ContentDialog card. Progress streams to the WebView settings panel.

    /// </summary>

    /// <summary>

    /// Check-only (never downloads/installs): the brain uses this on launch to

    /// ASK before updating. settings.checkUpdates remains the install path.

    /// </summary>

}

